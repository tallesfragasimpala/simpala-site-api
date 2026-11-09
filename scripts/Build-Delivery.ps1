param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $root 'artifacts\delivery' }
$name = 'simpala-site-infra-' + (Get-Date -Format 'yyyyMMdd-HHmmss')
$bundle = Join-Path $OutputDirectory $name
if (Test-Path -LiteralPath $bundle) { throw 'A pasta de entrega ja existe; nenhum arquivo foi sobrescrito.' }
New-Item -ItemType Directory -Path $bundle -Force | Out-Null
& dotnet publish (Join-Path $root 'src\Simpala.Site.Api') -c Release --no-restore -o (Join-Path $bundle 'API')
if ($LASTEXITCODE -ne 0) { throw 'Falha no publish; pacote nao gerado.' }
$sites = Join-Path (Split-Path $root -Parent) 'Sites'
$allowed = @('.html','.css','.js','.png','.jpg','.jpeg','.svg','.webp','.ico','.pdf','.woff','.woff2','.ttf','.eot','.txt')
foreach ($item in @(@{Source='simpala-financeira';Target='Financeira';Company='financeira'},@{Source='simpala-consorcios';Target='Consorcios';Company='consorcio'})) {
    $source = Join-Path $sites $item.Source
    if (-not (Test-Path (Join-Path $source 'index.html'))) { throw 'Pasta do site nao encontrada.' }
    $target = Join-Path $bundle ('Sites\'+$item.Target)
    foreach ($file in Get-ChildItem -LiteralPath $source -Recurse -File) {
        $relative = $file.FullName.Substring($source.Length).TrimStart('\')
        if ($relative -match '(^|[\\/])\.' -or $relative -match '(^|[\\/])(node_modules|tests)([\\/]|$)' -or $file.Extension.ToLowerInvariant() -notin $allowed) { continue }
        $destination = Join-Path $target $relative
        New-Item -ItemType Directory -Path (Split-Path $destination -Parent) -Force | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $destination
    }
    # Fail closed until the real API origin is supplied in the delivered configuration script.
    $config = "window.SIMPALA_CONTACT = Object.freeze({ apiBaseUrl: '', company: '$($item.Company)', requestScope: 'production', allowSending: false });"
    [IO.File]::WriteAllText((Join-Path $target 'contact-config.js'),$config,[Text.UTF8Encoding]::new($false))
    $index = Join-Path $target 'index.html'
    $html = [IO.File]::ReadAllText($index)
    $html = [regex]::Replace($html,'contact(-config)?\.js(?:\?v=[^"\s>]*)?',{param($match) ($match.Value -split '\?')[0]+'?v='+$name})
    [IO.File]::WriteAllText($index,$html,[Text.UTF8Encoding]::new($false))
}
New-Item -ItemType Directory -Path (Join-Path $bundle 'Scripts') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $root 'deploy\Configure-Server.ps1') -Destination (Join-Path $bundle 'Scripts')
Copy-Item -LiteralPath (Join-Path $root 'deploy\Configure-Websites.ps1') -Destination (Join-Path $bundle 'Scripts')
Copy-Item -LiteralPath (Join-Path $root 'deploy\LEIAME-INFRA.md') -Destination (Join-Path $bundle 'LEIAME-INFRA.md')
$forbidden = Get-ChildItem -LiteralPath $bundle -Recurse -File | Where-Object { $_.FullName -match '[\\/]App_Data[\\/]|[\\/]\.git[\\/]|\.db(-wal|-shm)?$|\.protected$|\.pfx$|\.p12$|\.pem$|\.key$|[\\/]\.env$' }
if ($forbidden) { throw 'Pacote possui arquivo privado; entrega nao compactada.' }
$hashes = foreach ($file in Get-ChildItem -LiteralPath $bundle -Recurse -File | Sort-Object FullName) {
    (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()+'  '+$file.FullName.Substring($bundle.Length+1)
}
[IO.File]::WriteAllLines((Join-Path $bundle 'SHA256SUMS.txt'),$hashes,[Text.UTF8Encoding]::new($false))
$archive = $bundle+'.zip'
Compress-Archive -LiteralPath $bundle -DestinationPath $archive
Write-Output ('Pacote gerado: '+$archive)
Write-Output ('SHA256: '+(Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash)
Write-Output 'Sem credenciais, dados de teste ou publicacao automatica. Dominio da API e pastas ainda precisam ser definidos pela infra.'
