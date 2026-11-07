param([string]$OutputDirectory = 'artifacts/plugin-submission')

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$source = Join-Path $repositoryRoot 'submission/agentrag'
$manifest = Get-Content -Raw -LiteralPath (Join-Path $source 'plugin.json') | ConvertFrom-Json -AsHashtable
$mcp = Get-Content -Raw -LiteralPath (Join-Path $source 'mcp.json') | ConvertFrom-Json -AsHashtable
$openai = $manifest.extensions.'com.openai'
$interface = $openai.interface
if ($manifest.name -ne 'agentrag' -or $manifest.version -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid package identity/version.' }
if ($interface.displayName.Length -gt 30 -or $interface.shortDescription.Length -gt 30 -or $interface.longDescription.Length -gt 4000) { throw 'Listing length limit exceeded.' }
if ($interface.category -ne 'Productivity') { throw 'Unexpected category.' }
if ($interface.defaultPrompt.Count -gt 3 -or @($interface.defaultPrompt | Where-Object { $_.Length -gt 128 -or $_ -match '[\r\n]' }).Count) { throw 'Invalid default prompts.' }
if ($manifest.ContainsKey('apps') -or $openai.ContainsKey('apps')) { throw 'Public uploads cannot include app bindings.' }
if ($openai.review.test_cases.positive.Count -ne 5 -or $openai.review.test_cases.negative.Count -ne 3) { throw 'Expected five positive and three negative cases.' }
if ($openai.review.commerce -ne $false -or $openai.publication.countries.Count -ne 0) { throw 'Commerce/country choices differ from the publisher instructions.' }
if ($null -ne $openai.publication.translations) {
    foreach ($locale in $openai.publication.translations.Keys) {
        if ([string]::IsNullOrWhiteSpace($locale)) { throw 'Translation locale cannot be blank.' }
        $translation = $openai.publication.translations[$locale]
        if ($null -eq $translation) { continue }
        if ($translation -isnot [System.Collections.IDictionary]) { throw "Translation $locale must be an object or null." }
        foreach ($field in $translation.Keys) {
            if ($field -notin @('subtitle', 'description')) { throw "Unsupported translation field: $locale.$field. Use subtitle or description." }
            $value = $translation[$field]
            if ($null -eq $value) { continue }
            if ($value -isnot [string] -or [string]::IsNullOrWhiteSpace($value) -or $value -match '[\x00-\x09\x0B\x0C\x0E-\x1F\x7F]') { throw "Invalid translated text: $locale.$field" }
            if (($field -eq 'subtitle' -and ($value.Length -gt 30 -or $value -match '[\r\n]')) -or
                ($field -eq 'description' -and $value.Length -gt 4000)) { throw "Translation length limit exceeded: $locale.$field" }
        }
    }
}
$server = $mcp.mcpServers.agentrag
if ($server.url -ne 'https://agentrag.runasp.net/mcp' -or $server.extensions.'com.openai'.auth.client.mode -ne 'dcr') { throw 'Unexpected MCP endpoint/authentication.' }

$output = [IO.Path]::GetFullPath((Join-Path $repositoryRoot $OutputDirectory))
if (-not $output.StartsWith($repositoryRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Output must remain inside the repository.' }
New-Item -ItemType Directory -Path $output -Force | Out-Null
$stage = Join-Path $output ('stage-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path (Join-Path $stage 'assets') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $source 'plugin.json') -Destination (Join-Path $stage 'plugin.json')
Copy-Item -LiteralPath (Join-Path $source 'mcp.json') -Destination (Join-Path $stage 'mcp.json')
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'plugins/agentrag/assets/icon.png') -Destination (Join-Path $stage 'assets/icon.png')

$missing = [Collections.Generic.List[string]]::new()
foreach ($field in @('websiteURL','supportURL','privacyPolicyURL','termsOfServiceURL')) {
    if (-not $interface.ContainsKey($field)) { $missing.Add("Verified public $field") }
    elseif ($interface[$field] -notmatch '^https://[^\s]+$') { throw "Invalid $field" }
}
if (-not $openai.review.ContainsKey('demo_recording_url')) { $missing.Add('Recorded demonstration at a verified reviewer-accessible HTTPS URL') }
$suffix = if ($missing.Count) { '-DRAFT' } else { '' }
$archive = Join-Path $output ("agentrag-$($manifest.version)$suffix.zip")
if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive -Force }
[IO.Compression.ZipFile]::CreateFromDirectory($stage, $archive)
$zip = [IO.Compression.ZipFile]::OpenRead($archive)
try {
    $entries = @($zip.Entries | ForEach-Object { $_.FullName.Replace('\','/') } | Sort-Object)
    if (($entries -join ',') -ne 'assets/icon.png,mcp.json,plugin.json') { throw 'Unexpected archive contents.' }
    foreach ($entryName in @('plugin.json','mcp.json')) {
        $reader = [IO.StreamReader]::new($zip.GetEntry($entryName).Open())
        try { $parsed = $reader.ReadToEnd() | ConvertFrom-Json -AsHashtable } finally { $reader.Dispose() }
        if (-not $parsed.ContainsKey('$schema')) { throw "Missing schema in $entryName" }
        if ($entryName -eq 'plugin.json' -and ($parsed | ConvertTo-Json -Depth 30 -Compress) -ne ($manifest | ConvertTo-Json -Depth 30 -Compress)) { throw 'The archived manifest differs from the validated source.' }
    }
}
finally { $zip.Dispose() }
$report = [ordered]@{
    archive = $archive; version = $manifest.version; publisher = $interface.developerName
    structuralValidation = 'passed'; readiness = $(if ($missing.Count) { 'draft; not ready for review submission' } else { 'package complete; portal validation still required' })
    missingPackageMaterials = @($missing); portalRequirements = @('Verified publisher identity and intended organization/project', 'Deployed hosted OAuth callback change', 'Domain verification and MCP scan', 'Review cases run against the saved submission', 'Authorized developer legal/policy attestations')
    entries = $entries; sha256 = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
}
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $output 'readiness.json') -Encoding utf8
Write-Host "Archive: $archive"
Write-Host "Structural validation passed. Submission readiness: $($report.readiness)"
foreach ($item in $missing) { Write-Host "Missing: $item" }
