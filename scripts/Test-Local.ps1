param([string]$BaseUrl = 'http://localhost:5099')
$ErrorActionPreference = 'Stop'
$challenge = Invoke-RestMethod -Uri "$BaseUrl/api/contact/challenge"
$numbers = [regex]::Matches($challenge.question, '[0-9]+')
$answer = 0
foreach ($number in $numbers) { $answer += [int]$number.Value }
$payload = @{
    name = 'Cliente Exemplo'
    email = 'cliente@exemplo.com'
    document = '52998224725'
    phone = '51999999999'
    message = 'Teste local sem envio.'
    contactPreference = ''
    challengeToken = $challenge.token
    challengeAnswer = [string]$answer
    website = ''
} | ConvertTo-Json
$key = [guid]::NewGuid().ToString()
$response = Invoke-RestMethod -Method Post -Uri "$BaseUrl/api/contact/financeira" `
    -Headers @{ 'Idempotency-Key' = $key } -ContentType 'application/json; charset=utf-8' `
    -Body ([System.Text.Encoding]::UTF8.GetBytes($payload))
$response | ConvertTo-Json -Depth 5
Write-Host 'Simulacao local. Nenhum e-mail foi enviado.'
