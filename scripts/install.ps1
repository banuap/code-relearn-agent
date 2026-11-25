<#
.SYNOPSIS
  Builds the relearn CLI and installs the ReLearn agents into a repository for Claude Code,
  GitHub Copilot (custom agents) and GitHub Spec Kit; prints BMAD module install steps.

.EXAMPLE
  .\scripts\install.ps1 -Repo C:\src\modernization -Tools claude,copilot -GlobalTool
#>
param(
  [Parameter(Mandatory = $true)] [string] $Repo,
  [string[]] $Tools = @("claude", "copilot"),
  [switch] $GlobalTool,
  [switch] $SkipBuild
)
$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
$Proj = Join-Path $Root "src\ReLearn.Analyzer\ReLearn.Analyzer.csproj"

if (-not $SkipBuild) {
  Write-Host "Building relearn CLI..." -ForegroundColor Cyan
  dotnet build $Proj -c Release | Out-Host
  if ($LASTEXITCODE -ne 0) { throw "dotnet build failed" }
}
if ($GlobalTool) {
  $nupkg = Join-Path $Root "artifacts\nupkg"
  dotnet pack $Proj -c Release -o $nupkg | Out-Host
  dotnet tool uninstall --global ReLearn.Analyzer 2>$null | Out-Null
  dotnet tool install --global --add-source $nupkg ReLearn.Analyzer | Out-Host
  $cli = "relearn"
} else {
  $cli = "dotnet `"$(Join-Path $Root 'src\ReLearn.Analyzer\bin\Release\net8.0\relearn.dll')`""
}

New-Item -ItemType Directory -Force -Path $Repo | Out-Null

function Copy-With-Cli($src, $dst) {
  New-Item -ItemType Directory -Force -Path (Split-Path -Parent $dst) | Out-Null
  $text = Get-Content $src -Raw
  $text = $text.Replace('$RELEARN', $cli).Replace('{relearn_cli}', $cli).Replace('<relearn-repo>', $Root)
  Set-Content -Path $dst -Value $text -Encoding utf8
  Write-Host "  + $dst"
}

if ($Tools -contains "claude") {
  Write-Host "Installing Claude Code agents, commands and skills..." -ForegroundColor Cyan
  Get-ChildItem "$Root\agents\claude\agents\*.md"   | ForEach-Object { Copy-With-Cli $_.FullName (Join-Path $Repo ".claude\agents\$($_.Name)") }
  Get-ChildItem "$Root\agents\claude\commands\*.md" | ForEach-Object { Copy-With-Cli $_.FullName (Join-Path $Repo ".claude\commands\$($_.Name)") }
  Copy-With-Cli "$Root\agents\speckit\commands\speckit.relearn.md" (Join-Path $Repo ".claude\commands\speckit.relearn.md")
  foreach ($s in "relearn-analyst", "relearn-extract", "relearn-handoff") {
    Copy-With-Cli "$Root\agents\bmad\skills\$s\SKILL.md" (Join-Path $Repo ".claude\skills\$s\SKILL.md")
  }
}

if ($Tools -contains "copilot") {
  Write-Host "Installing GitHub Copilot custom agents..." -ForegroundColor Cyan
  foreach ($f in Get-ChildItem "$Root\agents\claude\agents\*.md") {
    $name = [IO.Path]::GetFileNameWithoutExtension($f.Name)
    Copy-With-Cli $f.FullName (Join-Path $Repo ".github\agents\$name.agent.md")
  }
  Copy-With-Cli "$Root\agents\speckit\commands\speckit.relearn.md" (Join-Path $Repo ".github\agents\speckit.relearn.agent.md")
}

Write-Host ""
Write-Host "Done. CLI: $cli" -ForegroundColor Green
Write-Host "Next:"
Write-Host "  1. (optional) Spec Kit:   cd $Repo; uvx --from git+https://github.com/github/spec-kit.git specify init --here --ai claude"
Write-Host "  2. (optional) BMAD:       npx bmad-method install   then add the ReLearn module from $Root\agents\bmad\skills (bmod-relearn) and run: bmad setup relearn"
Write-Host "  3. In Claude Code:        /relearn C:\path\to\Legacy.sln java-spring ."
Write-Host "     or directly:           $cli all C:\path\to\Legacy.sln --target java-spring --out $Repo"
