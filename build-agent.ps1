# 에이전트 단일 exe 빌드 → publish\agent\HtUtil.Agent.exe
# 버전 = yy.MM.dd.n (n = 그날 빌드 횟수, 처음 0). 마지막 버전은 agent\version.txt에 기록.
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$today = Get-Date -Format 'yy.MM.dd'
$last = if (Test-Path agent\version.txt) { (Get-Content agent\version.txt -Raw).Trim() } else { '' }
$n = if ($last -like "$today.*") { [int]$last.Split('.')[3] + 1 } else { 0 }
$ver = "$today.$n"

if (-not (Test-Path agent\agent.json)) { throw "agent\agent.json이 없습니다 (setup.bat 먼저 실행). exe 안에 들어갈 공통 설정입니다." }
Write-Host "에이전트 빌드: v$ver" -ForegroundColor Cyan
# 이전 결과물 정리. 실행 중인 exe는 지울 수 없지만(Access denied) 이름은 바꿀 수 있으므로 .old로 비켜 둠
if (Test-Path publish\agent) {
    foreach ($f in Get-ChildItem publish\agent -File) {
        try { Remove-Item -LiteralPath $f.FullName -Force -ErrorAction Stop }
        catch { Move-Item -LiteralPath $f.FullName -Destination "$($f.FullName).$(Get-Date -Format HHmmss).old" -Force }
    }
}
# 단일 exe, .NET 런타임 포함 (설치 불필요). agent.json은 exe 안에 포함됨 (csproj EmbeddedResource)
dotnet publish agent -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none -p:Version=$ver -o publish\agent
if ($LASTEXITCODE) { throw "에이전트 빌드 실패" }
Remove-Item publish\agent\agent.json -ErrorAction SilentlyContinue # exe 안에 있으므로 exe 하나만 배포
Set-Content agent\version.txt $ver -Encoding ascii

$mb = (Get-Item publish\agent\HtUtil.Agent.exe).Length / 1MB
Write-Host ("`n완료: $PSScriptRoot\publish\agent\HtUtil.Agent.exe (v$ver, {0:N1}MB)" -f $mb) -ForegroundColor Green
Write-Host "새 PC 설치: 웹 관리자 화면 '다운로드 페이지에 올리기' → 작업자는 /download.html 에서 받기 (exe 하나, 설치 불필요)"
Write-Host "기존 PC 업데이트: 웹 관리자 화면 '에이전트 업데이트'에서 이 exe 선택 → 배포"
