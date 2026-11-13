# HtUtil — 1단계 프로토타입

```
agent/                 Windows 에이전트 (C#/.NET 9, WinForms + SIPSorcery WebRTC)
web/                   웹 뷰어 + 관리자 화면 (Firebase Hosting)
database.rules.json    RTDB 보안 규칙
firebase.json
```

## 설정
**빠른 설정:** 콘솔에서 아래 1번(프로젝트·RTDB·로그인 방법)만 한 뒤 `setup.bat` 실행 → Firebase 로그인, `web/config.js`·`agent/agent.json` 자동 생성, 규칙·웹 배포, 에이전트 배포 폴더(`publish\agent`, .NET 설치 불필요) 생성, 관리자 지정까지 진행. 필요: .NET 9 SDK, Node.js. 아래는 수동 절차.

1. Firebase 프로젝트 생성 → Realtime Database 생성, Authentication에서 **이메일/비밀번호**와 **익명** 로그인 사용 설정.
2. `web/config.example.js`를 `web/config.js`로 복사해 웹 앱 구성 값 입력 → `npx firebase-tools deploy --only database,hosting`
3. 관리자 만들기: 웹에서 아이디로 가입 (내부적으로 `아이디@htutil.local` 이메일 계정이 됨) → 콘솔에서 `users/{uid}/level`을 `9`로 수정.
4. `agent/agent.json.example`을 `agent/agent.json`으로 복사해 **공통 값**(apiKey, databaseUrl)만 입력 → 빌드. exe 하나에 설정이 들어감.
   - TURN(모바일 데이터 등 P2P 불가 시 중계): Cloudflare TURN 키를 만들고, 웹 관리자 화면 **TURN 중계**에 Turn Token ID / API Token 입력 → "온라인 PC에 보내기". 키는 exe·DB에 들어가지 않고 각 PC의 `turn.json`(서비스 모드는 `ProgramData\HtUtil`, 관리자만 읽기)에 저장. 새 PC는 설치 후 다시 보내야 함. 안 보내면 STUN만 사용.
5. PC 등록: 각 PC에서 에이전트 실행 → 익명 계정을 만들고 **자동 등록** (승인 없음). PC ID는 9자리 숫자, 이름은 "IP별 PC 이름" → 없으면 컴퓨터 이름.
   - 이름·minLevel·게스트 허용은 관리자 화면 "PC"에서 수정, 모르는 PC는 삭제.
   - "IP별 PC 이름"을 지정하면 그 IP의 기존 PC 이름도 바뀌고, 앞으로 등록되는 PC의 기본 이름이 됨.
   - 익명 계정의 로그인 토큰은 `%LOCALAPPDATA%\HtUtil\identity.json`에 저장 (프로그램 폴더 밖이라 폴더를 복사해도 따라가지 않음). 지우면 새 PC ID로 다시 등록됨.

## 새 PC에 에이전트 설치 (다운로드 페이지)
1. `build-agent.bat` 실행 → `publish\agent\HtUtil.Agent.exe` (설정 포함 단일 exe ~55MB, .NET 설치 불필요)
2. 웹 관리자 화면 "에이전트 업데이트"에서 exe 선택 → **다운로드 페이지에 올리기**
3. 작업자: `https://<프로젝트>.web.app/download.html` (로그인 불필요) → 받아서 실행 → UAC "예" → 서비스로 설치(잠금화면·로그인 화면에서도 원격, 재부팅 후 자동 실행) → 트레이 상주, PC 자동 등록
   - UAC "아니요"면 일반 모드 (잠금화면 원격 불가). 제거: 관리자 명령 프롬프트에서 `"C:\Program Files\HtUtil\HtUtil.Agent.exe" --uninstall`

## 에이전트 업데이트
1. `build-agent.bat` 실행 → `publish\agent\HtUtil.Agent.exe` (버전 `yy.MM.dd.n`, n은 그날 빌드 횟수 0부터)
2. 웹 관리자 화면 "에이전트 업데이트"에서 exe 선택 → **배포**
3. 지금 온라인이고 버전이 낮은 PC에만 바로 전송 → 각 에이전트가 교체 후 자동 재시작. 파일은 서버에 저장되지 않으므로 꺼져 있던 PC는 켜진 뒤 다시 배포.

## 동작 요약
- 뷰어가 `requests/{pcId}`에 요청 작성 (레벨·코드·게스트 허용은 **DB 규칙**이 검사)
- 에이전트가 레벨을 DB에서 재확인 → 필요 시 승인 창 (게스트, 레벨 1) → `sessions/`에 offer + ICE 서버 기록 → 요청을 `approved`로 변경
- 뷰어가 answer 작성 → P2P 연결 → 영상(VP8) + DataChannel 입력

## 스펙과 다른 점
- **온라인 표시**: REST에는 `onDisconnect`가 없어 30초 heartbeat(`lastSeen`) 사용. 90초 이상 갱신 없으면 오프라인.
- **게스트 코드**: `codeExpiresAt` 대신 서버 시간 `codeIssuedAt` + 규칙에서 5분 비교 (PC 시계 오차 무관).
- **PC 등록**: 에이전트가 익명 계정으로 `pcs/{pcId}` + `agents/{uid}`를 직접 생성 (규칙: 계정당 1대, 새 ID만, minLevel 2 / 게스트 불가로만). PC 정보는 `agentInfo/{uid}`.
- **로그인 실패 잠금**: 웹 화면에서 연속 5회 실패하면 잠김 (DB 규칙이 차단, 레벨 9 제외). 해제는 관리자 화면 "로그인 실패 / 잠긴 계정". 화면을 거치지 않는 직접 API 시도는 세지 못함.
- 계정 접속 승인: 레벨 9(관리자)와 **PC의 minLevel과 같은 레벨**은 승인 없이 접속, 그 밖의 레벨(1 이상)은 원격 PC에서 허용해야 함. 레벨 0은 접속 불가.

## 제한 (프로토타입)
- 주 모니터만, GDI 캡처 ~10fps. 동시 세션 1개.
- 키 입력은 브라우저 `keyCode` → VK 그대로 전달 (특수 키/레이아웃 일부 부정확 가능).
- 같은 LAN에서 테스트 시 Chrome이 로컬 IP를 mDNS(`*.local`)로 숨겨 연결이 안 될 수 있음 → TURN 설정 또는 다른 네트워크에서 테스트.
