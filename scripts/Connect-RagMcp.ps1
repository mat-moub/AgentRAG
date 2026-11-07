param(
    [Uri]$ApiUrl = 'http://localhost:5000',
    [ValidateRange(1, 120)]
    [int]$TimeoutSeconds = 15,
    [PSCredential]$Credential
)

$ErrorActionPreference = 'Stop'
if (-not $ApiUrl.IsAbsoluteUri -or $ApiUrl.UserInfo -or $ApiUrl.Query -or $ApiUrl.Fragment -or
    ($ApiUrl.Scheme -ne 'https' -and -not ($ApiUrl.Scheme -eq 'http' -and $ApiUrl.IsLoopback))) {
    throw 'Use an absolute HTTPS API URL, or HTTP on loopback, without credentials, query or fragment.'
}
$baseUrl = $ApiUrl.AbsoluteUri.TrimEnd('/')
try {
    if ($null -eq $Credential) {
        Write-Host "Sign in to AgentRAG at $baseUrl. Password characters are hidden. Press Ctrl+C to cancel."
        $email = Read-Host 'Email'
        if ([string]::IsNullOrWhiteSpace($email)) { throw 'Email is required.' }
        $password = Read-Host 'Password' -AsSecureString
        $Credential = [PSCredential]::new($email.Trim(), $password)
    }
    # Recent PowerShell versions separate connection and read timeouts.
    $timeoutOptions = @{ TimeoutSec = $TimeoutSeconds }
    if ((Get-Command Invoke-RestMethod).Parameters.ContainsKey('OperationTimeoutSeconds')) {
        $timeoutOptions = @{ ConnectionTimeoutSeconds = $TimeoutSeconds; OperationTimeoutSeconds = $TimeoutSeconds }
    }
    $body = @{ email = $credential.UserName; password = $credential.GetNetworkCredential().Password } | ConvertTo-Json
    Write-Host "Connecting to $baseUrl (network timeouts: $TimeoutSeconds seconds)..."
    try {
        $response = Invoke-RestMethod -Uri "$baseUrl/api/auth/login?useCookies=false" -Method Post -ContentType 'application/json' -Body $body -MaximumRedirection 0 @timeoutOptions
    }
    catch {
        $status = if ($_.Exception.Response) { [int]$_.Exception.Response.StatusCode } else { 0 }
        if ($status -eq 401) { throw 'Sign-in failed. Check your email/password and whether your account requires a two-factor code.' }
        if ($status -ge 300 -and $status -lt 400) { throw 'The API redirected the login request. Use its final URL directly.' }
        throw "Unable to sign in at $baseUrl (HTTP status: $status; 0 means no HTTP response). The API may be unavailable or the request timed out. Check the listening URL shown by the API. The default project profile uses http://localhost:5096; port 5000 requires an explicit --urls setting."
    }
    if ([string]::IsNullOrWhiteSpace($response.accessToken)) { throw 'No access token was returned.' }
    $env:AGENTRAG_MCP_TOKEN = $response.accessToken
    $env:AGENTRAG_MCP_URL = "$baseUrl/mcp"
    Write-Host "AgentRAG credentials are available in this shell. Token expires in $($response.expiresIn) seconds. Launch your MCP client from this shell."
}
finally {
    $body = $null
    $response = $null
    $credential = $null
    $password = $null
    $email = $null
}
