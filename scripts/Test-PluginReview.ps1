param([string]$AccessFile = 'artifacts/plugin-review-access.json')

$ErrorActionPreference = 'Stop'
$access = Get-Content -Raw -LiteralPath $AccessFile | ConvertFrom-Json
$token = $null
$script:reviewRpcId = 0
try {
    $login = Invoke-RestMethod ($access.apiUrl + '/api/auth/login?useCookies=false') -Method Post -ContentType 'application/json' -Body (@{email=$access.email;password=$access.password}|ConvertTo-Json) -TimeoutSec 30
    $token = $login.accessToken
    $headers = @{ Authorization = "Bearer $token"; Accept = 'application/json, text/event-stream' }
    function Invoke-ReviewMcp([string]$Method, [hashtable]$Parameters) {
        $script:reviewRpcId++
        $request = @{jsonrpc='2.0';id=$script:reviewRpcId;method=$Method;params=$Parameters}|ConvertTo-Json -Depth 12
        $response = Invoke-WebRequest ($access.apiUrl+'/mcp') -Method Post -ContentType 'application/json' -Headers $headers -Body $request -TimeoutSec 120
        $text = if ($response.Content -is [byte[]]) {[Text.Encoding]::UTF8.GetString($response.Content)} else {[string]$response.Content}
        if ($text.TrimStart().StartsWith('{')) { $message=$text|ConvertFrom-Json }
        else {
            $messages=@($text -split "`n" | Where-Object {$_ -match '^data: ' } | ForEach-Object {$_.Substring(6)|ConvertFrom-Json})
            $message=$messages|Where-Object {$_.id -eq $script:reviewRpcId}|Select-Object -Last 1
        }
        if ($null -eq $message -or $message.error) { throw "MCP $Method did not return a successful JSON-RPC result." }
        if ($message.result.isError) { throw "MCP $Method returned a tool error." }
        return $message.result
    }
    $initial = Invoke-ReviewMcp 'initialize' @{protocolVersion='2025-11-25';capabilities=@{};clientInfo=@{name='agentrag-submission-check';version='0.1.2'}}
    $headers['MCP-Protocol-Version']=$initial.protocolVersion
    $tools=Invoke-ReviewMcp 'tools/list' @{}
    $evidence=[ordered]@{ endpoint=($access.apiUrl+'/mcp'); checkedAtUtc=[DateTimeOffset]::UtcNow.ToString('O'); account='dedicated synthetic review account'; toolAnnotations=$tools.tools|Select-Object name,annotations; checks=@(); modelSelection='Not assessed: these are direct MCP probes, not host conversations.' }

    $listing=Invoke-ReviewMcp 'tools/call' @{name='list_documents';arguments=@{offset=0;limit=50}}
    $listed=($listing.content.text -join "`n")|ConvertFrom-Json
    if ($listed.Count -ne 2 -or $listed.title -notcontains 'Atlas Access Guide' -or $listed.title -notcontains 'Atlas Release Checklist') { throw 'Unexpected reviewer library.' }
    $evidence.checks+=@{name='Document discovery';status='Passed';result=$listed}
    Write-Host 'Passed: dedicated-account document discovery.'

    $page1=Invoke-ReviewMcp 'tools/call' @{name='list_documents';arguments=@{offset=0;limit=1}}
    $page2=Invoke-ReviewMcp 'tools/call' @{name='list_documents';arguments=@{offset=1;limit=1}}
    $first=($page1.content.text -join "`n")|ConvertFrom-Json
    $second=($page2.content.text -join "`n")|ConvertFrom-Json
    if ($first[0].id -eq $second[0].id) { throw 'Pagination repeated a document.' }
    $evidence.checks+=@{name='Pagination';status='Passed';result=@($first,$second)}
    Write-Host 'Passed: pagination returns distinct synthetic documents.'

    $search=Invoke-ReviewMcp 'tools/call' @{name='search_knowledge_base';arguments=@{query='Who approves access to Atlas and how long does it take?';top=2}}
    $passages=($search.content.text -join "`n")|ConvertFrom-Json
    if (-not ($passages|Where-Object {$_.documentTitle -eq 'Atlas Access Guide' -and $_.content -match 'two working days'})) { throw 'Expected access passage was not returned.' }
    $evidence.checks+=@{name='Semantic passage search';status='Passed';result=$passages}
    Write-Host 'Passed: semantic search returns the expected source passage.'

    foreach ($question in @(
        @{name='Grounded release answer';text='What checks are required before deploying Atlas? Cite the document supporting the answer.';sources=@('Atlas Release Checklist')},
        @{name='Cross-document synthesis';text='Compare the responsibilities for approving Atlas access and preparing an Atlas release. Cite each source.';sources=@('Atlas Access Guide','Atlas Release Checklist')}
    )) {
        $answer=Invoke-ReviewMcp 'tools/call' @{name='ask_rag';arguments=@{question=$question.text}}
        $answerText=$answer.content.text -join "`n"
        foreach ($source in $question.sources) { if ($answerText -notmatch [regex]::Escape($source)) { throw "Answer omitted expected citation: $source" } }
        $evidence.checks+=@{name=$question.name;status='Passed';result=$answerText}
        Write-Host "Passed: $($question.name)."
    }
    $evidence|ConvertTo-Json -Depth 15|Set-Content -LiteralPath 'artifacts/plugin-submission/direct-tool-evidence.json' -Encoding utf8
    Write-Host 'Direct MCP probes passed. Host model-selection cases and the saved portal version remain to be tested.'
}
finally { $token=$null; $access=$null; $headers=$null; $login=$null }
