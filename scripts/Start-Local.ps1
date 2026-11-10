$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot '..\src\Simpala.Site.Api'
$dataPath = [System.IO.Path]::GetFullPath((Join-Path $project 'App_Data\Local'))
$variables = @{
    'EmailSettings__EnableSending' = 'true'
    'Contact__EnablePublicAccess' = 'false'
    'Contact__DataDirectory' = $dataPath
}
$original = @{}
foreach ($name in @($variables.Keys) + @('SupportMail__ClientSecret', 'SupportMail__EncryptionKey', 'SupportMail__EncryptedClientSecret')) {
    $original[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}
$settings = Get-Content (Join-Path $project 'appsettings.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$encrypted = $settings.SupportMail.EncryptedClientSecret
$prompt = if ($encrypted) { 'Chave da Criptool (entrada oculta; somente nesta sessao)' } else { 'Client secret (entrada oculta; somente nesta sessao)' }
$secret = Read-Host $prompt -AsSecureString
$pointer = [IntPtr]::Zero
try {
    if ($secret.Length -eq 0) { throw 'Informe o valor do client secret.' }
    $pointer = [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($secret)
    $inputValue = [System.Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer)
    if ($encrypted) {
        [Environment]::SetEnvironmentVariable('SupportMail__EncryptionKey', $inputValue, 'Process')
        [Environment]::SetEnvironmentVariable('SupportMail__ClientSecret', $null, 'Process')
        [Environment]::SetEnvironmentVariable('SupportMail__EncryptedClientSecret', $encrypted, 'Process')
        $aes = [System.Security.Cryptography.Aes]::Create()
        $decryptor = $null
        $plainBytes = $null
        try {
            $keyBytes = [Text.Encoding]::UTF8.GetBytes($inputValue)
            $ivBytes = [Text.Encoding]::UTF8.GetBytes($settings.SupportMail.EncryptionIV)
            if ($keyBytes.Length -ne 32 -or $ivBytes.Length -ne 16) { throw 'Formato invalido.' }
            $aes.Key = $keyBytes
            $aes.IV = $ivBytes
            $aes.Mode = [Security.Cryptography.CipherMode]::CBC
            $aes.Padding = [Security.Cryptography.PaddingMode]::PKCS7
            $decryptor = $aes.CreateDecryptor()
            $cipherBytes = [Convert]::FromBase64String($encrypted)
            $plainBytes = $decryptor.TransformFinalBlock($cipherBytes, 0, $cipherBytes.Length)
            $preflightSecret = [Text.UTF8Encoding]::new($false, $true).GetString($plainBytes)
        }
        catch { throw 'Nao foi possivel descriptografar. Confira a chave da Criptool, o IV e o valor configurado.' }
        finally {
            if ($plainBytes) { [Array]::Clear($plainBytes, 0, $plainBytes.Length) }
            if ($keyBytes) { [Array]::Clear($keyBytes, 0, $keyBytes.Length) }
            if ($decryptor) { $decryptor.Dispose() }
            $aes.Dispose()
        }
    } else {
        $preflightSecret = $inputValue
        [Environment]::SetEnvironmentVariable('SupportMail__ClientSecret', $inputValue, 'Process')
    }
    $inputValue = $null
    foreach ($name in $variables.Keys) {
        [Environment]::SetEnvironmentVariable($name, $variables[$name], 'Process')
    }
    # Token-only preflight: reject invalid credentials before starting the public form endpoint.
    $settings = Get-Content (Join-Path $project 'appsettings.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    $tenant = [Environment]::GetEnvironmentVariable('SupportMail__TenantId', 'Process')
    $client = [Environment]::GetEnvironmentVariable('SupportMail__ClientId', 'Process')
    if (-not $tenant) { $tenant = $settings.SupportMail.TenantId }
    if (-not $client) { $client = $settings.SupportMail.ClientId }
    $tokenForm = @{
        grant_type = 'client_credentials'
        scope = 'https://graph.microsoft.com/.default'
        client_id = ([guid]$client).ToString()
        client_secret = $preflightSecret
    }
    $authResult = $null
    try {
        $authResult = Invoke-RestMethod -Method Post -Uri ('https://login.microsoftonline.com/' + ([guid]$tenant).ToString() + '/oauth2/v2.0/token') -ContentType 'application/x-www-form-urlencoded' -Body $tokenForm -TimeoutSec 30
        if (-not $authResult.access_token) { throw 'AUTH_RESPONSE_INVALID' }
        Write-Host 'Autenticacao Microsoft validada. Nenhum e-mail enviado.' -ForegroundColor Green
    }
    catch {
        $authCode = 'indisponivel'
        try {
            $details = $_.ErrorDetails.Message | ConvertFrom-Json
            if ($details.error_codes.Count -gt 0 -and ([string]$details.error_codes[0]) -match '^[0-9]+$') {
                $authCode = [string]$details.error_codes[0]
            }
        } catch { }
        Write-Host ('Falha na autenticacao Microsoft. Codigo Entra: ' + $authCode + '. Confira o valor e a validade do segredo. Nenhum e-mail enviado.') -ForegroundColor Red
        throw 'A API nao foi iniciada: autenticacao nao validada.'
    }
    finally {
        $tokenForm.Clear()
        $preflightSecret = $null
        $authResult = $null
    }
    Write-Host 'API iniciada em Production para teste local. Enviar o formulario envia um e-mail real.' -ForegroundColor Yellow
    Write-Host 'Apenas iniciar o servidor nao envia e-mails. Encerre com Ctrl+C.'
    & dotnet run --project $project --no-launch-profile -- --environment Production --urls https://localhost:7099 --Contact:AllowedOrigins:0=http://localhost:8000 --Contact:AllowedOrigins:1=http://127.0.0.1:8000 --Contact:AllowedOrigins:2=http://localhost:5500 --Contact:AllowedOrigins:3=http://127.0.0.1:5500
    if ($LASTEXITCODE -ne 0) { throw 'A API nao concluiu a execucao normalmente. Confira a mensagem acima.' }
}
finally {
    foreach ($name in $original.Keys) {
        [Environment]::SetEnvironmentVariable($name, $original[$name], 'Process')
    }
    if ($pointer -ne [IntPtr]::Zero) { [System.Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer) }
    $secret.Dispose()
}
