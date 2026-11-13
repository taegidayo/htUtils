# HtUtil Agent 버전 확인 (읽기만 함, 아무것도 바꾸지 않음)
# 사용: 이 파일 우클릭 → PowerShell에서 실행, 또는 check-agent.bat (서비스 프로세스까지 보려면 관리자 권한)

$exe = Join-Path $env:ProgramFiles 'HtUtil\HtUtil.Agent.exe'
$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

Write-Host ''
Write-Host '== 설치된 파일 ==' -ForegroundColor Cyan
$installed = $null
if (Test-Path $exe) {
    $f = Get-Item $exe
    $installed = $f.VersionInfo.FileVersion
    Write-Host ("  {0}  v{1}  (수정 {2})" -f $exe, $installed, $f.LastWriteTime)
    Get-ChildItem (Split-Path $exe) -Filter 'HtUtil.Agent.exe.old*' -ErrorAction SilentlyContinue |
        ForEach-Object { Write-Host ("  남은 옛 파일: {0}  v{1}" -f $_.Name, $_.VersionInfo.FileVersion) -ForegroundColor Yellow }
} else {
    Write-Host '  서비스 설치 안 됨 (Program Files에 없음) - 일반 모드로만 실행 중일 수 있음'
}

Write-Host ''
Write-Host '== 서비스 ==' -ForegroundColor Cyan
$svc = Get-Service HtUtilAgent -ErrorAction SilentlyContinue
if ($svc) { Write-Host ("  HtUtilAgent: {0}" -f $svc.Status) } else { Write-Host '  HtUtilAgent 서비스 없음' }

Write-Host ''
Write-Host '== 실행 중인 에이전트 ==' -ForegroundColor Cyan
$procs = @(Get-Process HtUtil.Agent -ErrorAction SilentlyContinue)
if (-not $procs) { Write-Host '  실행 중인 HtUtil.Agent 없음' -ForegroundColor Yellow }
$stale = $false
foreach ($p in $procs) {
    $role = if ($p.SessionId -eq 0) { '서비스 본체' } else { "세션 $($p.SessionId)" }
    try {
        $path = $p.MainModule.FileName
        $ver = $p.MainModule.FileVersionInfo.FileVersion
        $start = $p.StartTime
    } catch {
        Write-Host ("  PID {0} ({1}): 관리자 권한으로 실행해야 볼 수 있음" -f $p.Id, $role) -ForegroundColor Yellow
        continue
    }
    if (-not $path) { # SYSTEM 프로세스는 관리자가 아니면 빈 값으로 옴
        Write-Host ("  PID {0} ({1}): 관리자 권한으로 실행해야 볼 수 있음" -f $p.Id, $role) -ForegroundColor Yellow
        continue
    }
    $mark = ''
    if ($installed -and $path -like '*Program Files*' -and $ver -ne $installed) { $mark = '  <- 설치된 파일과 다른 옛 버전'; $stale = $true }
    Write-Host ("  PID {0,-6} {1,-10} v{2,-14} 시작 {3}  {4}{5}" -f $p.Id, $role, $ver, $start, $path, $mark)
}

Write-Host ''
if ($stale) {
    Write-Host '결과: 옛 버전이 아직 실행 중입니다. 관리자 명령 프롬프트에서 아래 실행 (또는 재부팅):' -ForegroundColor Yellow
    Write-Host '  sc stop HtUtilAgent'
    Write-Host '  sc start HtUtilAgent'
} elseif (-not $admin -and $svc) {
    Write-Host '참고: 관리자 권한이 아니면 서비스 프로세스 버전은 안 보일 수 있습니다.' -ForegroundColor DarkGray
} else {
    Write-Host '결과: 문제 없음' -ForegroundColor Green
}
