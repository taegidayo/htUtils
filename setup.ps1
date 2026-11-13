# HtUtil 한 번에 설정: setup.bat에서 실행됨.
# Firebase 설정값 받아오기 → web/config.js, agent/agent.json 생성 → 규칙·웹 배포 → 에이전트 배포 폴더 생성
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

function Step($s) { Write-Host "`n== $s" -ForegroundColor Cyan }
function Need($cmd, $url) {
    if (-not (Get-Command $cmd -ErrorAction SilentlyContinue)) { throw "$cmd 이(가) 설치되어 있지 않습니다: $url" }
}
# 대화형 firebase 명령 (로그인 등)
function Fb { & npx --yes firebase-tools @args; if ($LASTEXITCODE) { throw "firebase $args 실패" } }
# --json 결과의 result만 반환 (npm 안내문은 stderr라 버림)
function FbJson {
    $out = cmd /c "npx --yes firebase-tools $args --json 2>nul" | Out-String
    $j = $out | ConvertFrom-Json
    if ($j.status -ne 'success') { throw "firebase $args 실패: $out" }
    $j.result
}

Write-Host @"
HtUtil 설정

시작 전에 Firebase 콘솔(https://console.firebase.google.com)에서 직접 해야 하는 것:
  1. 프로젝트 생성
  2. Realtime Database 생성 (위치: asia-southeast1 권장, 잠금 모드로 시작해도 됨)
  3. Authentication > 로그인 방법: '이메일/비밀번호'와 '익명' 사용 설정
"@
Read-Host "준비되면 Enter" | Out-Null

Step "필수 프로그램 확인"
Need dotnet 'https://dotnet.microsoft.com/download/dotnet/9.0 (.NET 9 SDK)'
Need npx 'https://nodejs.org (LTS)'

Step "Firebase 로그인"
Fb login

Step "프로젝트 선택"
$project = ''
if (Test-Path .firebaserc) { $project = (Get-Content .firebaserc -Raw | ConvertFrom-Json).projects.default }
$in = Read-Host "Firebase 프로젝트 ID [$project]"
if ($in) { $project = $in.Trim() }
if (-not $project) { throw "프로젝트 ID가 필요합니다." }
Set-Content .firebaserc "{ `"projects`": { `"default`": `"$project`" } }" -Encoding ascii

Step "Realtime Database 확인"
$db = @(FbJson database:instances:list --project $project) | Where-Object { $_.type -eq 'DEFAULT_DATABASE' } | Select-Object -First 1
if (-not $db) { throw "Realtime Database가 없습니다. 콘솔에서 먼저 생성하세요." }
Write-Host $db.databaseUrl

Step "웹 앱 설정값 받기"
$app = @(FbJson apps:list WEB --project $project) | Select-Object -First 1
if (-not $app) { $app = FbJson apps:create WEB HtUtil --project $project }
$cfg = (FbJson apps:sdkconfig WEB $app.appId --project $project).sdkConfig
$dbUrl = if ($cfg.databaseURL) { $cfg.databaseURL } else { $db.databaseUrl }

$utf8 = New-Object System.Text.UTF8Encoding $false
[IO.File]::WriteAllText("$PSScriptRoot\web\config.js", @"
// setup.bat이 생성 (Firebase 웹 앱 구성 값)
export const firebaseConfig = {
  apiKey: "$($cfg.apiKey)",
  authDomain: "$($cfg.authDomain)",
  databaseURL: "$dbUrl",
  projectId: "$($cfg.projectId)",
};
"@, $utf8)
Write-Host "web\config.js 생성"

# TURN 키는 여기 넣지 않음 → 웹 관리자 화면 'TURN 중계'에서 PC로 직접 전송
[IO.File]::WriteAllText("$PSScriptRoot\agent\agent.json", (@{
    apiKey = $cfg.apiKey; databaseUrl = $dbUrl
} | ConvertTo-Json), $utf8)
Write-Host "agent\agent.json 생성"

Step "DB 규칙 + 웹 페이지 배포"
Fb deploy --only database,hosting --project $project

Step "에이전트 빌드"
& "$PSScriptRoot\build-agent.ps1"

Step "관리자 지정 (선택)"
Write-Host "웹 페이지에서 관리자로 쓸 아이디로 먼저 '가입'한 뒤, Firebase 콘솔 > Authentication에서 그 사용자의 UID를 복사하세요."
$uid = Read-Host "관리자 UID (건너뛰려면 Enter)"
if ($uid) { Fb database:set "/users/$($uid.Trim())/level" --data 9 --force --project $project }

Write-Host @"

완료!
  웹 페이지   : https://$project.web.app
  에이전트    : $PSScriptRoot\publish\agent\HtUtil.Agent.exe  (설정 포함 단일 exe, .NET 설치 불필요)
               웹 관리자 화면에서 이 exe 선택 → '다운로드 페이지에 올리기' → 작업자는 https://$project.web.app/download.html
  업데이트    : build-agent.bat → 웹 관리자 화면 '에이전트 업데이트'에서 exe 선택 → 배포
"@ -ForegroundColor Green
