[CmdletBinding()]
param(
    [string]$Runtime = "win-x64",
    [switch]$SkipBundle,
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]*$')]
    [string]$PackageName = "ProjectBridge"
)

$ErrorActionPreference = "Stop"
$RepositoryRoot = Split-Path -Parent $PSScriptRoot
$PublishRoot = Join-Path $RepositoryRoot "artifacts\$PackageName"
$ArtifactsRoot = [IO.Path]::GetFullPath((Join-Path $RepositoryRoot "artifacts"))
$ResolvedPublishRoot = [IO.Path]::GetFullPath($PublishRoot)
if (-not $ResolvedPublishRoot.StartsWith($ArtifactsRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "发布目录不在 artifacts 内。"
}

if (Test-Path -LiteralPath $ResolvedPublishRoot) { Remove-Item -LiteralPath $ResolvedPublishRoot -Recurse -Force }
New-Item -ItemType Directory -Path $PublishRoot -Force | Out-Null

dotnet publish (Join-Path $RepositoryRoot "src\LocalProjectBridge\LocalProjectBridge.csproj") `
    -c Release -r $Runtime --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $PublishRoot
if ($LASTEXITCODE -ne 0) { throw "ProjectBridge 发布失败。" }

dotnet publish (Join-Path $RepositoryRoot "src\CodexShim\CodexShim.csproj") `
    -c Release -r $Runtime --self-contained true `
    -p:PublishSingleFile=true `
    -o $PublishRoot
if ($LASTEXITCODE -ne 0) { throw "Codex 启动组件发布失败。" }

dotnet publish (Join-Path $RepositoryRoot "src\ProjectBridge.Relay\ProjectBridge.Relay.csproj") `
    -c Release -r $Runtime --self-contained true `
    -p:PublishSingleFile=true `
    -o $PublishRoot
if ($LASTEXITCODE -ne 0) { throw "本地协作助手发布失败。" }

if (-not $SkipBundle) {
    $BackendSource = Join-Path $env:LOCALAPPDATA "LocalProjectBridge\backends"
    $BackendTarget = Join-Path $PublishRoot "backends"
    $RuntimeTarget = Join-Path $PublishRoot "runtime"
    # 版本与 THIRD-PARTY-NOTICES.md 保持一致；更新组件时同步修改两处。
    $PinnedBackendCommits = @{
        "transceiver" = "c822936958e7b084e35afaee1035f4f6ec0427a2"
        "codex-with-chatgpt" = "9663b88753e35c76796c5bce000293e0bd22cd9e"
    }
    foreach ($BackendName in $PinnedBackendCommits.Keys) {
        $SourcePath = Join-Path $BackendSource $BackendName
        if (-not (Test-Path -LiteralPath $SourcePath)) { throw "缺少发布组件：$BackendName" }
        $SourceCommit = (& git -C $SourcePath rev-parse HEAD).Trim()
        if ($LASTEXITCODE -ne 0 -or $SourceCommit -ne $PinnedBackendCommits[$BackendName]) {
            throw "$BackendName 版本与第三方声明不一致：$SourceCommit"
        }
        if (& git -C $SourcePath status --porcelain --untracked-files=no) {
            throw "$BackendName 含未提交的修改，与第三方声明不一致。"
        }
        $TargetPath = Join-Path $BackendTarget $BackendName
        New-Item -ItemType Directory -Path $TargetPath -Force | Out-Null
        $ExcludedDirectories = @(".git", "node_modules", ".tooling")

        if ($BackendName -eq "codex-with-chatgpt") {
            $PackageManifest = Get-Content -LiteralPath (Join-Path $SourcePath "package.json") -Raw | ConvertFrom-Json
            $PackageManager = [string]$PackageManifest.packageManager
            if (-not $PackageManager.StartsWith("pnpm@", [StringComparison]::OrdinalIgnoreCase)) {
                throw "C2C 未声明受支持的 pnpm 版本。"
            }
            $Corepack = Join-Path (Split-Path -Parent (Get-Command node.exe -ErrorAction Stop).Source) "corepack.cmd"
            & $Corepack $PackageManager --dir $SourcePath install --frozen-lockfile
            if ($LASTEXITCODE -ne 0) { throw "安装 C2C 构建依赖失败。" }
            & $Corepack $PackageManager --dir $SourcePath build
            if ($LASTEXITCODE -ne 0) { throw "编译 C2C 失败。" }
        }

        & robocopy.exe $SourcePath $TargetPath /E /XD $ExcludedDirectories /NFL /NDL /NJH /NJS /NP | Out-Null
        if ($LASTEXITCODE -gt 7) { throw "复制发布组件失败：$BackendName" }

        if ($BackendName -eq "codex-with-chatgpt") {
            & $Corepack $PackageManager --dir $TargetPath install --prod --frozen-lockfile --node-linker=hoisted --package-import-method=copy
            if ($LASTEXITCODE -ne 0) { throw "安装 C2C 发布依赖失败。" }

            $LinkedRuntimeDependencies = Get-ChildItem -LiteralPath (Join-Path $TargetPath "node_modules") -Force -Recurse |
                Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }
            if ($LinkedRuntimeDependencies) {
                throw "C2C 发布依赖包含不可移植的链接。"
            }
        }
    }

    $BundledC2c = Join-Path $BackendTarget "codex-with-chatgpt"
    $BundledC2cCli = Join-Path $BundledC2c "bin\c2c.js"
    if (-not (Test-Path -LiteralPath (Join-Path $BundledC2c "node_modules\commander"))) {
        throw "发布包缺少 C2C 运行依赖。"
    }
    & (Get-Command node.exe -ErrorAction Stop).Source $BundledC2cCli --help | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "发布包中的 C2C 无法启动。" }
    $BundledSkill = Join-Path $PublishRoot "skills\projectbridge-chatgpt\SKILL.md"
    if (-not (Test-Path -LiteralPath $BundledSkill)) { throw "发布包缺少 Codex 协作 Skill。" }
    if (-not (Select-String -LiteralPath $BundledSkill -SimpleMatch "{{PROJECTBRIDGE_RELAY_PATH}}" -Quiet)) {
        throw "Codex 协作 Skill 模板缺少 Relay 路径占位符。"
    }

    New-Item -ItemType Directory -Path $RuntimeTarget -Force | Out-Null
    $RuntimeFiles = @(
        (Join-Path $env:LOCALAPPDATA "Programs\tunnel-client\full\cloudflared.exe"),
        (Join-Path $env:LOCALAPPDATA "Programs\tunnel-client\full\tunnel-client.exe"),
        (Join-Path $env:LOCALAPPDATA "Programs\tunnel-client\full\LICENSE"),
        (Join-Path $env:LOCALAPPDATA "Programs\tunnel-client\full\NOTICE"),
        (Join-Path $env:LOCALAPPDATA "Programs\tunnel-client\full\cloudflared-manifest.json"),
        (Join-Path $env:LOCALAPPDATA "Programs\tunnel-client\full\tunnel-client-v0.0.13-windows-amd64-licenses.txt"),
        (Join-Path $env:LOCALAPPDATA "Programs\tunnel-client\full\tunnel-client-v0.0.13-windows-amd64.spdx.json")
    )
    foreach ($RuntimeFile in $RuntimeFiles) {
        if (-not (Test-Path -LiteralPath $RuntimeFile)) { throw "缺少发布运行组件：$RuntimeFile" }
        Copy-Item -LiteralPath $RuntimeFile -Destination $RuntimeTarget -Force
    }
    foreach ($Readme in @("README.md", "README_en.md")) {
        Copy-Item -LiteralPath (Join-Path $RepositoryRoot $Readme) -Destination $PublishRoot -Force
    }
    $PublishedDocs = Join-Path $PublishRoot "docs"
    New-Item -ItemType Directory -Path $PublishedDocs -Force | Out-Null
    foreach ($Doc in @("guide.md", "technical.md", "auto-consult.md")) {
        Copy-Item -LiteralPath (Join-Path $RepositoryRoot "docs\$Doc") -Destination $PublishedDocs -Force
    }
    Copy-Item -LiteralPath (Join-Path $RepositoryRoot "THIRD-PARTY-NOTICES.md") -Destination $PublishRoot -Force
    Copy-Item -LiteralPath (Join-Path $RepositoryRoot "LICENSE") -Destination $PublishRoot -Force
    Copy-Item -LiteralPath (Join-Path $RepositoryRoot "third-party\transceiver\SOURCE-INFO.md") -Destination (Join-Path $BackendTarget "transceiver") -Force

    $ArchivePath = Join-Path (Split-Path -Parent $PublishRoot) "$PackageName.zip"
    if (Test-Path -LiteralPath $ArchivePath) { Remove-Item -LiteralPath $ArchivePath -Force }
    & tar.exe -a -c -f $ArchivePath -C $PublishRoot .
    if ($LASTEXITCODE -ne 0) { throw "创建免安装包失败。" }

    $VerificationRoot = Join-Path $ArtifactsRoot ("verify-" + [guid]::NewGuid().ToString("N"))
    try {
        New-Item -ItemType Directory -Path $VerificationRoot -Force | Out-Null
        & tar.exe -x -f $ArchivePath -C $VerificationRoot
        if ($LASTEXITCODE -ne 0) { throw "解压免安装包失败。" }

        $ExtractedC2c = Join-Path $VerificationRoot "backends\codex-with-chatgpt"
        if (-not (Test-Path -LiteralPath (Join-Path $ExtractedC2c "node_modules\commander"))) {
            throw "免安装包缺少 C2C 运行依赖。"
        }
        & (Get-Command node.exe -ErrorAction Stop).Source (Join-Path $ExtractedC2c "bin\c2c.js") --help | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "免安装包中的 C2C 无法启动。" }
        $ExtractedRelay = Join-Path $VerificationRoot "ProjectBridge.Relay.exe"
        if (-not (Test-Path -LiteralPath $ExtractedRelay)) { throw "免安装包缺少本地协作助手。" }
        & $ExtractedRelay --help | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "免安装包中的协作助手无法启动。" }
        $ExtractedSkill = Join-Path $VerificationRoot "skills\projectbridge-chatgpt\SKILL.md"
        if (-not (Test-Path -LiteralPath $ExtractedSkill)) { throw "免安装包缺少 Codex 协作 Skill。" }
    }
    finally {
        $ResolvedVerificationRoot = [IO.Path]::GetFullPath($VerificationRoot)
        if ($ResolvedVerificationRoot.StartsWith($ArtifactsRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) `
            -and (Test-Path -LiteralPath $ResolvedVerificationRoot)) {
            Remove-Item -LiteralPath $ResolvedVerificationRoot -Recurse -Force
        }
    }
    Write-Host "免安装包：$ArchivePath"
}

Write-Host "发布完成：$PublishRoot"
