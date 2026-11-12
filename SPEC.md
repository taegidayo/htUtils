# 원격 제어 프로그램 — 설계 문서 (프로토타입)

## 목표
- 서버는 **두 PC의 연결만 중개**(시그널링)하고, 화면·입력은 **PC 간 WebRTC P2P**로 주고받는다.
- 서버 리소스 최소화, **상업적 사용도 무료**인 서비스만 사용.
- 대상: Windows PC 약 10대.

## 기술 스택 (전부 무료)
| 역할 | 서비스 |
|---|---|
| 시그널링 / PC 목록 / 접속 기록 | Firebase Realtime Database |
| 로그인 (계정 + 게스트용 익명 인증) | Firebase Auth |
| 웹 뷰어 · 관리자 화면 호스팅 | Firebase Hosting |
| STUN | `stun:stun.cloudflare.com:3478` (무료·무제한) |
| TURN (P2P 실패 시 중계) | Cloudflare Realtime TURN (월 1,000GB 무료) |
| 원격 PC 에이전트 | Windows 앱 (1단계: 일반 앱 / 2단계: Windows 서비스 + 세션 도우미) |

> 참고: Vercel Hobby는 상업적 사용 금지라 제외. Oracle Cloud 제외(사용자 요청).
> Firebase Spark 한도: RTDB 동시 연결 100개(10대 규모엔 충분), 다운로드 10GB/월. 한도 초과 시 앱이 정지되므로 사용량 모니터링 필요.
> Cloud Functions는 유료(Blaze)라 사용하지 않음 → TURN 자격 증명 발급 방식은 구현 시 결정(예: 에이전트/관리자 측 발급).

## 컴포넌트
1. **웹 뷰어** (브라우저, Firebase Hosting)
   - 접속 방식 선택: **게스트 접속** / **계정 접속**
   - 온라인 PC 목록(계정 접속 시), 화면 보기, 마우스·키보드 전송(DataChannel)
2. **관리자 화면** (레벨 9)
   - 사용자 레벨 변경, PC별 `minLevel` / 게스트 허용 설정, 접속 기록 조회
3. **Windows 에이전트**
   - 온라인 상태 등록(`onDisconnect`로 자동 오프라인 처리)
   - 게스트용 PC ID + 일회용 코드 표시(수 분마다 갱신), 접속 승인 창
   - 화면 캡처 → WebRTC 영상 트랙, DataChannel 입력 → 마우스/키보드 주입
   - 원격 중 화면에 "원격 제어 중" 표시
   - 요청자 레벨을 DB에서 재확인(이중 검증)

## 접속 방식
| | 게스트 접속 (로그인 없음) | 계정 접속 (로그인) |
|---|---|---|
| 입력 | PC ID + 일회용 코드 | 계정 로그인 |
| 인증 | Firebase 익명 인증 | Firebase 계정 |
| 상대 승인 | **필요** (원격 PC에 허용/거부 창) | **불필요** |
| 권한 확인 | 코드 일치 + 원격 PC 승인 | `users/{uid}/level >= pcs/{pcId}/minLevel` |
| PC별 설정 | `allowGuest: false`면 게스트 차단 | — |

## 계정 레벨 (예시)
| 레벨 | 권한 |
|---|---|
| 0 | 로그인만, 원격 불가 |
| 1 | 원격 요청 가능 (상대 승인 필요) |
| 2 | 승인 없이 원격 (무인 접속) |
| 9 | 관리자 (레벨·PC 관리) |

레벨 변경: 관리자 화면 또는 Firebase 콘솔에서 `users/{uid}/level` 직접 수정.

## DB 구조 (초안)
```
/users/{uid}            { name, level }
/pcs/{pcId}             { name, online, lastSeen, minLevel, allowGuest }
/pcSecrets/{pcId}       { code, codeExpiresAt }        ← 클라이언트 읽기 불가, 규칙에서만 비교
/requests/{pcId}/{reqId}{ uid, mode: "guest"|"account", code?, status: "pending"|"approved"|"denied" }
/sessions/{pcId}/{sid}  { offer, answer, callerCandidates, calleeCandidates }
/logs/{logId}           { uid, mode, pcId, startedAt, endedAt }
```

## 보안 규칙 핵심
- `users/$uid/level` 쓰기는 **레벨 9만** 가능 (본인 레벨 상향 금지 — 최우선).
- 계정 접속 세션 쓰기: 요청자 레벨 ≥ 해당 PC `minLevel`.
- 게스트 요청: `newData.code === root/pcSecrets/$pcId/code` 이고 만료 전, `allowGuest == true`.
- 게스트 세션 쓰기: 해당 요청이 에이전트에 의해 `approved`이고 요청자 `auth.uid` 일치할 때만.
- `pcSecrets`는 해당 PC 에이전트 계정만 읽기/쓰기.
- 버튼 숨김이 아니라 **DB 규칙에서 차단**해야 함.

## 단계
- **1단계 (프로토타입)**: 로그인된 Windows 세션 원격. 웹 뷰어, 관리자 화면, DB 규칙, 에이전트(일반 앱).
- **2단계**: 에이전트를 Windows 서비스(SYSTEM)로 설치 + 사용자 세션에 캡처/입력 도우미 프로세스 실행 → 잠금 화면·로그인 화면·UAC 지원.

## 미정 사항 (구현 시 결정)
- 에이전트 구현 언어/프레임워크 (예: Electron, C#/.NET, Rust, Go+pion)
- 에이전트의 Firebase 인증 방식(PC별 서비스 계정 등)
- TURN 자격 증명 발급 위치 (Cloud Functions 미사용 전제)
