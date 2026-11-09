param(
    [Parameter(Mandatory=$true)][string]$ApplicationPath,
    [Parameter(Mandatory=$true)][string]$DataDirectory,
    [Parameter(Mandatory=$true)][string]$ApiOrigin,
    [Parameter(Mandatory=$true)][string]$AppPoolName,
    [switch]$EnablePublicAccess
)
$ErrorActionPreference = 'Stop'
$admin = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $admin.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Execute como administrador no servidor Windows.' }
$origin = [uri]$ApiOrigin
if (-not $origin.IsAbsoluteUri -or $origin.Scheme -ne 'https' -or $origin.AbsolutePath -ne '/' -or $origin.Query -or $origin.Fragment -or $origin.UserInfo -or $origin.IsLoopback) { throw 'Informe a origem HTTPS real da API, sem caminho ou credenciais.' }
foreach ($directory in @($ApplicationPath,$DataDirectory)) {
    if (-not [IO.Path]::IsPathRooted($directory)) { throw 'As pastas devem ter caminhos absolutos.' }
    $resolved = [IO.Path]::GetFullPath($directory).TrimEnd('\')
    if ($resolved -eq [IO.Path]::GetPathRoot($resolved).TrimEnd('\') -or $resolved.StartsWith($env:windir,[StringComparison]::OrdinalIgnoreCase)) { throw 'Escolha uma pasta dedicada para a API e seus dados.' }
}
$ApplicationPath = [IO.Path]::GetFullPath($ApplicationPath).TrimEnd('\')
$DataDirectory = [IO.Path]::GetFullPath($DataDirectory).TrimEnd('\')
if ($DataDirectory -eq $ApplicationPath -or $DataDirectory.StartsWith($ApplicationPath+'\',[StringComparison]::OrdinalIgnoreCase) -or $ApplicationPath.StartsWith($DataDirectory+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'A pasta de dados precisa ficar separada da pasta publicada.' }
$assembly = Join-Path $ApplicationPath 'Simpala.Site.Api.dll'
$configPath = Join-Path $ApplicationPath 'appsettings.json'
$webPath = Join-Path $ApplicationPath 'web.config'
if (-not (Test-Path -LiteralPath $assembly) -or -not (Test-Path -LiteralPath $configPath) -or -not (Test-Path -LiteralPath $webPath)) { throw 'Copie o pacote API para ApplicationPath antes de configurar.' }
Import-Module WebAdministration
if (-not (Test-Path ('IIS:\AppPools\'+$AppPoolName))) { throw 'Crie primeiro o Application Pool dedicado no IIS.' }
if ((Get-WebAppPoolState -Name $AppPoolName).Value -ne 'Stopped') { throw 'Pare o Application Pool da API antes de configurar. O script nao para servicos automaticamente.' }
$poolIdentity = [Security.Principal.NTAccount]::new('IIS AppPool\'+$AppPoolName)
$poolSid = $poolIdentity.Translate([Security.Principal.SecurityIdentifier])
$pool = Get-Item ('IIS:\AppPools\'+$AppPoolName)
if (([string]$pool.processModel.identityType) -notin @('4','ApplicationPoolIdentity')) { throw 'Este script exige ApplicationPoolIdentity; para identidade personalizada, solicitar revisao da infra.' }
if ($pool.enable32BitAppOnWin64) { throw 'Desative Enable 32-Bit Applications para este pacote.' }
$config = Get-Content -LiteralPath $configPath -Raw -Encoding UTF8 | ConvertFrom-Json
[xml]$web = Get-Content -LiteralPath $webPath -Raw
$asp = $web.SelectSingleNode('//aspNetCore')
if (-not $asp) { throw 'web.config sem modulo ASP.NET Core.' }
foreach ($node in $asp.SelectNodes('environmentVariables/environmentVariable')) {
    if ($node.name -match '^(SupportMail|Contact|EmailSettings)__|^AllowedHosts$') { throw 'Ha variaveis antigas no web.config que podem sobrescrever a configuracao. Revisar antes de continuar.' }
}
if ((Test-Path -LiteralPath $DataDirectory) -and (Get-ChildItem -LiteralPath $DataDirectory -Force | Select-Object -First 1)) { throw 'A primeira instalacao exige pasta de dados exclusiva e vazia. Nenhum dado existente sera alterado.' }
$secretDirectory = Join-Path $DataDirectory 'secrets'
$secretFile = Join-Path $secretDirectory 'mail.protected'
if (Test-Path -LiteralPath $secretFile) { throw 'O segredo protegido ja existe. Este script e para primeira instalacao; nao sobrescreve credenciais existentes.' }
function Set-PrivateDirectoryAcl([string]$Path,[Security.AccessControl.FileSystemRights]$PoolRights) {
    $acl = [Security.AccessControl.DirectorySecurity]::new()
    $acl.SetAccessRuleProtection($true,$false)
    $inherit = [Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit'
    foreach ($sid in @('S-1-5-18','S-1-5-32-544')) {
        $identity = [Security.Principal.SecurityIdentifier]::new($sid)
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($identity,'FullControl',$inherit,'None','Allow'))
    }
    $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($poolSid,$PoolRights,$inherit,'None','Allow'))
    Set-Acl -LiteralPath $Path -AclObject $acl
}
New-Item -ItemType Directory -Path $DataDirectory -Force | Out-Null
Set-PrivateDirectoryAcl $DataDirectory 'Modify'
New-Item -ItemType Directory -Path $secretDirectory -Force | Out-Null
Set-PrivateDirectoryAcl $secretDirectory 'ReadAndExecute'
Write-Host 'Informe o segredo SOMENTE neste servidor. Ele sera protegido pelo Windows; nao sera incluido no pacote.'
& dotnet $assembly --configure-mail-secret $secretFile
if ($LASTEXITCODE -ne 0) { throw 'Nao foi possivel configurar o segredo. A configuracao da API nao foi ativada.' }
$config.EmailSettings.EnableSending = $true
$config.Contact.EnablePublicAccess = [bool]$EnablePublicAccess
$config.Contact | Add-Member NoteProperty DataDirectory $DataDirectory -Force
$config.Contact.AllowedOrigins = @('https://www.simpalafinanceira.com.br','https://www.simpalaconsorcios.com.br')
$config.AllowedHosts = $origin.DnsSafeHost
$config.SupportMail | Add-Member NoteProperty ClientSecretFile $secretFile -Force
if ($config.SupportMail.PSObject.Properties.Name -contains 'ClientSecret') { $config.SupportMail.PSObject.Properties.Remove('ClientSecret') }
$utf8 = [Text.UTF8Encoding]::new($false)
[IO.File]::WriteAllText($configPath,($config | ConvertTo-Json -Depth 10),$utf8)
$variables = $asp.SelectSingleNode('environmentVariables')
if (-not $variables) { $variables = $web.CreateElement('environmentVariables'); $asp.AppendChild($variables) | Out-Null }
foreach ($name in @('ASPNETCORE_ENVIRONMENT','DOTNET_ENVIRONMENT')) {
    $item = $variables.SelectSingleNode("environmentVariable[@name='$name']")
    if (-not $item) { $item = $web.CreateElement('environmentVariable'); $item.SetAttribute('name',$name); $variables.AppendChild($item) | Out-Null }
    $item.SetAttribute('value','Production')
}
$web.Save($webPath)
$appAcl = Get-Acl -LiteralPath $ApplicationPath
$appAcl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($poolSid,'ReadAndExecute','ContainerInherit,ObjectInherit','None','Allow'))
Set-Acl -LiteralPath $ApplicationPath -AclObject $appAcl
Set-ItemProperty ('IIS:\AppPools\'+$AppPoolName) -Name processModel.loadUserProfile -Value $true
Write-Host 'Configuracao concluida. Nenhum e-mail enviado e nenhum servico iniciado.' -ForegroundColor Green
Write-Host ('Acesso publico habilitado: '+[bool]$EnablePublicAccess)
Write-Host 'A infra ainda deve conferir DNS, certificado HTTPS, binding e iniciar o pool para validar.'
