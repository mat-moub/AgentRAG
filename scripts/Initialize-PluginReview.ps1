param(
    [Parameter(Mandatory)][string]$Email,
    [Uri]$ApiUrl = 'https://agentrag.runasp.net',
    [string]$AccessFile = 'artifacts/plugin-review-access.json'
)

$ErrorActionPreference = 'Stop'
if (-not $ApiUrl.IsAbsoluteUri -or $ApiUrl.Scheme -ne 'https' -or $ApiUrl.UserInfo -or $ApiUrl.Query -or $ApiUrl.Fragment) {
    throw 'Use the HTTPS AgentRAG API origin.'
}
$baseUrl = $ApiUrl.AbsoluteUri.TrimEnd('/')
$token = $null
$password = $null
try {
    if (Test-Path -LiteralPath $AccessFile) {
        $access = Get-Content -Raw -LiteralPath $AccessFile | ConvertFrom-Json
        if ($access.email -ne $Email -or $access.apiUrl -ne $baseUrl) { throw 'The existing review account belongs to a different target.' }
        $password = $access.password
    }
    else {
        $password = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(24)) + '!aA9'
        $body = @{ email = $Email; password = $password } | ConvertTo-Json
        try { Invoke-RestMethod "$baseUrl/api/auth/register" -Method Post -ContentType 'application/json' -Body $body -TimeoutSec 30 | Out-Null }
        catch { throw 'Could not register the dedicated review account. No existing account was changed.' }
        $absoluteAccessFile = [IO.Path]::GetFullPath($AccessFile)
        New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($absoluteAccessFile)) -Force | Out-Null
        @{ email = $Email; password = $password; apiUrl = $baseUrl; loginUrl = "$baseUrl/account/login" } |
            ConvertTo-Json | Set-Content -LiteralPath $absoluteAccessFile -Encoding utf8
        if ($IsWindows) {
            $acl = [Security.AccessControl.FileSecurity]::new()
            $acl.SetAccessRuleProtection($true, $false)
            $identity = [Security.Principal.WindowsIdentity]::GetCurrent().User
            $acl.SetOwner($identity)
            $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($identity, 'FullControl', 'Allow'))
            Set-Acl -LiteralPath $absoluteAccessFile -AclObject $acl
        }
    }
    $body = @{ email = $Email; password = $password } | ConvertTo-Json
    $login = Invoke-RestMethod "$baseUrl/api/auth/login?useCookies=false" -Method Post -ContentType 'application/json' -Body $body -TimeoutSec 30
    $token = $login.accessToken
    if ([string]::IsNullOrWhiteSpace($token)) { throw 'Review account did not receive a login token.' }
    $headers = @{ Authorization = "Bearer $token" }
    $existing = Invoke-RestMethod "$baseUrl/api/documents" -Headers $headers -TimeoutSec 30
    foreach ($fixture in @(
        @{ title = 'Atlas Access Guide'; path = 'submission/review/fixtures/atlas-access-guide.md' },
        @{ title = 'Atlas Release Checklist'; path = 'submission/review/fixtures/atlas-release-checklist.md' }
    )) {
        if ($existing.title -contains $fixture.title) { continue }
        $body = @{ title = $fixture.title; content = Get-Content -Raw -LiteralPath $fixture.path } | ConvertTo-Json
        Write-Host "Indexing synthetic review fixture: $($fixture.title)"
        $response = Invoke-WebRequest "$baseUrl/api/documents/ingest" -Method Post -ContentType 'application/json' -Body $body -Headers $headers -TimeoutSec 180
        $eventText = if ($response.Content -is [byte[]]) { [Text.Encoding]::UTF8.GetString($response.Content) } else { [string]$response.Content }
        $events = @($eventText -split "`n" | Where-Object { $_.Trim() } | ForEach-Object { $_ | ConvertFrom-Json })
        if (-not ($events | Where-Object { $_.type -eq 'completed' })) {
            $failure = $events | Where-Object { $_.type -eq 'error' } | Select-Object -First 1
            throw "The fixture did not finish indexing: $($failure.error). The account can be reused on retry."
        }
    }
    $documents = Invoke-RestMethod "$baseUrl/api/documents" -Headers $headers -TimeoutSec 30
    if ($documents.Count -ne 2 -or $documents.title -notcontains 'Atlas Access Guide' -or $documents.title -notcontains 'Atlas Release Checklist') {
        throw 'Unexpected content in the review account; inspect it before sharing reviewer access.'
    }
    Write-Host 'Review account verified: password login works; exactly two synthetic documents are indexed.'
    Write-Host "Private credentials are stored in $AccessFile. Do not include this file in the ZIP or listing."
}
finally {
    $token = $null; $password = $null; $body = $null; $login = $null; $access = $null; $headers = $null
}
