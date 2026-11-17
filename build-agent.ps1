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
# exe 안에는 공개 값(apiKey, databaseUrl)만 넣음. TURN 키는 관리자 웹에서 PC로 직접 전송 (exe는 공개 다운로드되므로)
$cfg = Get-Content agent\agent.json -Raw -Encoding utf8 | ConvertFrom-Json
New-Item -ItemType Directory -Force agent\obj | Out-Null
[IO.File]::WriteAllText("$PSScriptRoot\agent\obj\agent.public.json", (@{ apiKey = $cfg.apiKey; databaseUrl = $cfg.databaseUrl } | ConvertTo-Json))
# 업데이트 서명 키 (ECDSA P-256): 비밀 키 keys\update-signing.key (gitignore, 이 PC에만 — 꼭 백업),
# 공개 키 agent\update-signing.pub (exe에 들어감). 에이전트는 이 키로 서명된 업데이트만 설치 → 관리자 계정이 털려도 가짜 exe 배포 불가.
$curve = [Security.Cryptography.ECCurve+NamedCurves]::nistP256
$keyFile = "$PSScriptRoot\keys\update-signing.key"
if (-not (Test-Path $keyFile)) {
    New-Item -ItemType Directory -Force (Split-Path $keyFile) | Out-Null
    $p = ([Security.Cryptography.ECDsa]::Create($curve)).ExportParameters($true)
    Set-Content $keyFile ([Convert]::ToBase64String($p.D) + ' ' + [Convert]::ToBase64String([byte[]]($p.Q.X + $p.Q.Y))) -Encoding ascii
    Write-Host "새 업데이트 서명 키를 만들었습니다: $keyFile" -ForegroundColor Yellow
    Write-Host "  → USB 등에 꼭 백업하세요. 잃어버리면 웹 업데이트가 막혀 PC마다 직접 다시 설치해야 합니다." -ForegroundColor Yellow
}
$keyParts = (Get-Content $keyFile -Raw).Trim().Split(' ')
Set-Content agent\update-signing.pub $keyParts[1] -Encoding ascii # 비밀 키에서 항상 다시 씀 (둘이 어긋나지 않게)

# 단일 exe, .NET 런타임 포함 (설치 불필요)
dotnet publish agent -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none -p:Version=$ver -o publish\agent
if ($LASTEXITCODE) { throw "에이전트 빌드 실패" }
Remove-Item publish\agent\agent.json -ErrorAction SilentlyContinue # exe 안에 있으므로 exe 하나만 배포

# exe 서명 → HtUtil.Agent.exe.sig (웹 업데이트 때 exe와 같이 선택)
$q = [Convert]::FromBase64String($keyParts[1])
$pt = New-Object Security.Cryptography.ECPoint
$pt.X = [byte[]]$q[0..31]; $pt.Y = [byte[]]$q[32..63]
$prm = New-Object Security.Cryptography.ECParameters
$prm.Curve = $curve; $prm.D = [Convert]::FromBase64String($keyParts[0]); $prm.Q = $pt
$sig = ([Security.Cryptography.ECDsa]::Create($prm)).SignData([IO.File]::ReadAllBytes("$PSScriptRoot\publish\agent\HtUtil.Agent.exe"), [Security.Cryptography.HashAlgorithmName]::SHA256)
Set-Content publish\agent\HtUtil.Agent.exe.sig ([Convert]::ToBase64String($sig)) -Encoding ascii
Set-Content agent\version.txt $ver -Encoding ascii

$mb = (Get-Item publish\agent\HtUtil.Agent.exe).Length / 1MB
Write-Host ("`n완료: $PSScriptRoot\publish\agent\HtUtil.Agent.exe (v$ver, {0:N1}MB)" -f $mb) -ForegroundColor Green
Write-Host "새 PC 설치: 웹 관리자 화면 '다운로드 페이지에 올리기' → 작업자는 /download.html 에서 받기 (exe 하나, 설치 불필요)"
Write-Host "기존 PC 업데이트: 웹 관리자 화면 '에이전트 업데이트'에서 HtUtil.Agent.exe와 HtUtil.Agent.exe.sig를 함께 선택 → 배포"
