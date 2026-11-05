param(
    [string]$TenantId,
    [string]$ClientId
)

$ErrorActionPreference = 'Stop'
$configPath = Join-Path $PSScriptRoot '..\src\Simpala.Site.Api\appsettings.json'
$config = Get-Content -LiteralPath $configPath -Raw -Encoding UTF8 | ConvertFrom-Json
if (-not $TenantId) { $TenantId = $config.SupportMail.TenantId }
if (-not $ClientId) { $ClientId = $config.SupportMail.ClientId }
$tenantGuid = [guid]::Empty
$clientGuid = [guid]::Empty
if (-not [guid]::TryParse($TenantId, [ref]$tenantGuid) -or
    -not [guid]::TryParse($ClientId, [ref]$clientGuid)) {
    throw 'Configure TenantId e ClientId validos na secao SupportMail do appsettings.json.'
}

$secureSecret = Read-Host 'Client secret (valor; entrada oculta)' -AsSecureString
if ($secureSecret.Length -eq 0) {
    $secureSecret.Dispose()
    throw 'O client secret nao pode ficar vazio.'
}
$secretPointer = [IntPtr]::Zero
$form = $null
$tokenResponse = $null
try {
    $secretPointer = [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($secureSecret)
    $form = @{
        grant_type = 'client_credentials'
        scope = 'https://graph.microsoft.com/.default'
        client_id = $clientGuid.ToString()
        client_secret = [System.Runtime.InteropServices.Marshal]::PtrToStringBSTR($secretPointer)
    }
    $tokenUrl = 'https://login.microsoftonline.com/' + $tenantGuid.ToString() + '/oauth2/v2.0/token'
    $tokenResponse = Invoke-RestMethod -Method Post -Uri $tokenUrl `
        -ContentType 'application/x-www-form-urlencoded' -Body $form -TimeoutSec 30
    if (-not $tokenResponse.access_token) { throw 'AUTH_RESPONSE_INVALID' }
    Write-Host 'Autenticacao no Entra: OK' -ForegroundColor Green
    Write-Host ('Validade informada do token: ' + $tokenResponse.expires_in + ' segundos')
    Write-Host 'Nenhum e-mail foi enviado. O token e o segredo nao serao exibidos.'
    Write-Host 'Este teste nao confirma a permissao de envio nem o escopo das caixas.'
}
catch {
    # Do not print HTTP bodies, exception objects, credentials or the access token.
    Write-Host 'Falha na autenticacao. Confira os IDs, o valor e a validade do segredo, a rede e as politicas do tenant.' -ForegroundColor Red
    exit 1
}
finally {
    if ($form) { $form.Clear() }
    $tokenResponse = $null
    if ($secretPointer -ne [IntPtr]::Zero) {
        [System.Runtime.InteropServices.Marshal]::ZeroFreeBSTR($secretPointer)
    }
    $secureSecret.Dispose()
}
