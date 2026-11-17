import { initializeApp } from "https://www.gstatic.com/firebasejs/11.0.2/firebase-app.js";
import { initializeAuth, inMemoryPersistence, onAuthStateChanged, signInAnonymously, signInWithEmailAndPassword, createUserWithEmailAndPassword, signOut,
  EmailAuthProvider, reauthenticateWithCredential }
  from "https://www.gstatic.com/firebasejs/11.0.2/firebase-auth.js";
import { getDatabase, ref, get, set, update, push, onValue, onChildAdded, off, query, orderByChild, limitToLast, serverTimestamp, runTransaction, onDisconnect }
  from "https://www.gstatic.com/firebasejs/11.0.2/firebase-database.js";
import { firebaseConfig } from "./config.js";

const app = initializeApp(firebaseConfig);
// 로그인 상태를 메모리에만 보관 → 페이지를 닫거나 새로고침하면 항상 로그아웃 상태로 시작
const auth = initializeAuth(app, { persistence: inMemoryPersistence }), db = getDatabase(app);
const $ = id => document.getElementById(id);
const status = s => $("status").textContent = s;
const fail = e => status("오류: " + (e.code || e.message || e));

let serverOffset = 0;
onValue(ref(db, ".info/serverTimeOffset"), s => serverOffset = s.val() || 0);
const isOnline = p => p.online && p.lastSeen > Date.now() + serverOffset - 90000; // agent heartbeat = 30s

// PC 상태 배지: 원격 중이면 누가·언제부터 (에이전트가 pcs/{id}/remote에 기록, 끝나면 지움)
function pcState(p) {
  if (!isOnline(p)) return el("span", { textContent: "오프라인", className: "off" });
  if (!p.remote) return el("span", { textContent: "온라인", className: "on" });
  const since = new Date(p.remote.since).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" });
  return el("span", { textContent: `원격 중 · ${p.remote.who} (${since}~)`, className: "busy", title: `${p.remote.who} 님이 ${since}부터 원격 제어 중` });
}
function row(tbody, cells) {
  const tr = tbody.insertRow();
  for (const c of cells) tr.insertCell().append(c ?? "");
  return tr;
}
function el(tag, props = {}, on = {}) {
  const e = Object.assign(document.createElement(tag), props);
  for (const [k, f] of Object.entries(on)) e.addEventListener(k, f);
  return e;
}

// ---------- 로그인 ----------
$("gGo").onclick = async () => {
  const pcId = $("gPc").value.trim(), code = $("gCode").value.replace(/\D/g, "");
  if (!pcId || code.length !== 6) return status("PC ID와 6자리 코드를 입력하세요.");
  try {
    if (!auth.currentUser) await signInAnonymously(auth);
    await connect(pcId, "guest", code);
  } catch (e) { fail(e); }
};
// 아이디만 입력 → 고정 도메인을 붙여 Firebase 이메일 계정으로 사용 ('@'가 있으면 그대로)
const LOGIN_DOMAIN = "htutil.local";
const toEmail = id => (id = id.trim()).includes("@") ? id : `${id}@${LOGIN_DOMAIN}`;
const toId = email => email.endsWith("@" + LOGIN_DOMAIN) ? email.slice(0, -LOGIN_DOMAIN.length - 1) : email;
// 로그인 실패 잠금: 화면이 loginFails에 연속 실패 횟수 기록, 5회 이상이면 DB 규칙이 그 계정을 차단 (레벨 9 제외).
// ponytail: 화면을 거치지 않는 REST 직접 시도는 세지 못함 — 진짜 잠금은 Cloud Functions 필요.
const MAX_FAILS = 5;
const failKey = email => email.toLowerCase().replaceAll(".", ","); // 규칙: auth.token.email.replace('.', ',')
const LOCKED = "로그인 실패가 많아 잠긴 계정입니다. 관리자에게 문의하세요.";
$("signIn").onclick = async () => {
  const email = toEmail($("email").value);
  try { await signInWithEmailAndPassword(auth, email, $("pw").value); }
  catch (e) {
    if (e.code !== "auth/invalid-credential" && e.code !== "auth/wrong-password") return fail(e);
    try {
      if (!auth.currentUser) await signInAnonymously(auth); // 기록하려면 로그인 필요
      const { snapshot } = await runTransaction(ref(db, `loginFails/${failKey(email)}`), n => (n || 0) + 1);
      const n = snapshot.val();
      status(n >= MAX_FAILS ? LOCKED : `아이디 또는 비밀번호가 틀렸습니다. (${n}/${MAX_FAILS})`);
    } catch (e2) { fail(e2); }
  }
};
// 가입 버튼 없음: 관리자가 "계정 만들기"로 만듦. (Firebase는 apiKey로 직접 가입을 막지 못함 → 그렇게 만든 계정은
// 프로필이 레벨 0이라 규칙상 PC 목록도 못 봄. 완전히 막으려면 Firebase 콘솔 Authentication → 설정에서 가입 끄기)
$("signOut").onclick = () => logout();

// ---------- 로그인 세션 (presence) ----------
// 로그인한 창마다 presence/{uid}/{sid} = {ip, ua, at}. 창을 닫거나 연결이 끊기면 서버가 지움(onDisconnect), 로그아웃 때는 직접 지움.
// 강제 로그아웃 = users/{uid}/logoutBefore = 지금: 그 전에 로그인한 창은 스스로 로그아웃하고, DB 규칙도 PC 목록·원격 요청을 막음.
let presRef = null, loginAt = 0, relogging = false;
// 로그인 칸 비우기: 로그인 직후와 로그아웃(강제 포함) 때 — 화면에 아이디·비밀번호가 남지 않게
const clearLogin = () => ["email", "pw", "gPc", "gCode", "reauthPw"].forEach(id => $(id).value = "");
async function logout(msg) {
  clearLogin();
  if (msg) status(msg);
  if (presRef) { await set(presRef, null).catch(() => {}); presRef = null; }
  await signOut(auth);
}
// ponytail: 공인 IP·국가는 브라우저가 모름 → 외부 서비스(ipapi.co)에 물어봄. 브라우저가 알려 주는 값이라 위조 가능 (참고·화면 차단용).
// 진짜 해외 차단은 에이전트가 연결된 IP로 검사함 (GeoKr.cs).
const whereAmI = () => fetch("https://ipapi.co/json/", { signal: AbortSignal.timeout(4000) }).then(r => r.json())
  .then(j => ({ ip: j.ip || "?", cc: j.country_code || "" }), () => ({ ip: "?", cc: "" }));
function device() {
  const ua = navigator.userAgent;
  const os = /Android/.test(ua) ? "Android" : /iPhone|iPad/.test(ua) ? "iOS" : /Windows/.test(ua) ? "Windows" : /Mac/.test(ua) ? "Mac" : /Linux/.test(ua) ? "Linux" : "?";
  const br = /Edg\//.test(ua) ? "Edge" : /SamsungBrowser/.test(ua) ? "Samsung" : /Chrome\//.test(ua) ? "Chrome" : /Firefox\//.test(ua) ? "Firefox" : /Safari\//.test(ua) ? "Safari" : "?";
  return `${br} · ${os}` + (matchMedia("(display-mode: standalone)").matches ? " (앱)" : "");
}
// 이 창만 남기고 모두 로그아웃: 비밀번호 확인 → logoutBefore = 지금 → 이 창은 같은 비밀번호로 다시 로그인(새 로그인 시각)
$("logoutOthers").onclick = () => { $("reauth").hidden = !$("reauth").hidden; $("reauthPw").focus(); };
$("reauthGo").onclick = async () => {
  const u = auth.currentUser, pw = $("reauthPw").value;
  if (!pw) return status("비밀번호를 입력하세요.");
  try { await reauthenticateWithCredential(u, EmailAuthProvider.credential(u.email, pw)); }
  catch { return status("비밀번호가 맞지 않습니다."); }
  relogging = true;
  try {
    await set(ref(db, `users/${u.uid}/logoutBefore`), serverTimestamp());
    await sleep(1200); // 규칙은 초 단위 로그인 시각과 비교 → 다음 초에 다시 로그인
    await signInWithEmailAndPassword(auth, u.email, pw);
    loginAt = Date.parse((await auth.currentUser.getIdTokenResult(true)).authTime);
    $("reauthPw").value = ""; $("reauth").hidden = true;
    status("이 창을 제외한 모든 곳에서 로그아웃했습니다.");
  } catch (e) { fail(e); }
  finally { relogging = false; }
};
// Enter 키: 아이디/비밀번호 칸 → 로그인, 게스트 코드 칸 → 접속 (한글 입력 조합 중의 Enter는 무시)
const onEnter = (ids, btn) => ids.forEach(id => $(id).addEventListener("keydown", e => e.key === "Enter" && !e.isComposing && $(btn).click()));
onEnter(["email", "pw"], "signIn");
onEnter(["gPc", "gCode"], "gGo");
onEnter(["reauthPw"], "reauthGo");

let unsubs = [];
onAuthStateChanged(auth, async user => {
  unsubs.forEach(u => u()); unsubs = [];
  const account = user && !user.isAnonymous;
  if (account) clearLogin();
  $("login").hidden = account; $("pcs").hidden = !account; $("admin").hidden = true;
  if (!account) return;
  const name = toId(user.email);
  $("me").textContent = name;
  const me = ref(db, `users/${user.uid}`);
  const failRef = ref(db, `loginFails/${failKey(user.email)}`);
  const fails = (await get(failRef).catch(() => null))?.val() || 0;
  const prof = await get(me).catch(e => { // 잠긴 계정은 규칙이 읽기를 막음 — 그 외 실패는 화면에 표시
    if (fails < MAX_FAILS) status("프로필을 읽지 못했습니다: " + (e.code || e.message));
    return null;
  });
  if (fails >= MAX_FAILS && prof?.val()?.level !== 9) return logout(LOCKED);
  if (prof?.val()?.deleted) return logout("관리자가 삭제한 계정입니다.");
  loginAt = Date.parse((await user.getIdTokenResult()).authTime);
  if (!presRef) {
    presRef = push(ref(db, `presence/${user.uid}`));
    onDisconnect(presRef).remove();
    const r = presRef;
    whereAmI().then(async ({ ip, cc }) => {
      // 해외 IP 차단 (관리자 설정, 기본 켜짐): 웹에서도 바로 로그아웃. 국가를 모르면(조회 실패) 통과 — 원격은 에이전트가 다시 막음
      const block = (await get(ref(db, "settings/blockForeign")).catch(() => null))?.val() !== false;
      if (block && cc && cc !== "KR") return logout(`해외(${cc})에서는 접속할 수 없습니다.`);
      if (r === presRef) await set(r, { ip, cc, ua: device(), at: serverTimestamp() });
    }).catch(() => {});
  }
  if (fails) set(failRef, null).catch(() => {}); // 로그인 성공 → 연속 실패 횟수 초기화
  // 최초 로그인: 레벨 0으로 프로필 생성 (자식 경로별 규칙이 적용되도록 multi-path update)
  if (prof && !prof.exists()) await update(ref(db), { [`users/${user.uid}/name`]: name, [`users/${user.uid}/level`]: 0 }).catch(fail);
  let pcs = {}, admin = false, blocked = false, level = 0;
  const render = () => {
    const tb = $("pcList"); tb.replaceChildren();
    if (level < 1) { row(tb, ["관리자가 이 계정의 레벨을 정하면 PC 목록이 보입니다.", "", "", ""]); return; }
    for (const [id, p] of Object.entries(pcs)) {
      const on = isOnline(p), actions = el("span");
      if (on && blocked) actions.append(el("span", { textContent: "원격 차단됨 (관리자 문의)", className: "off" }));
      else if (on) actions.append(el("button", { textContent: "원격 접속", className: "primary" }, { click: () => connect(id, "account").catch(fail) }));
      if (admin && on && p.remote) actions.append(el("button", { textContent: "강제 해제", className: "danger" }, { click: () => kickPc(id, p) }));
      if (admin) actions.append( // 관리자: 이름 변경 / 삭제 (DB 규칙도 레벨 9만 허용)
        el("button", { textContent: "이름 변경" }, { click: () => renamePc(id, p.name) }),
        el("button", { textContent: "삭제", className: "danger" }, { click: () => deletePc(id, p) }));
      row(tb, [`${p.name || id} (${id})`, pcState(p),
        String(p.minLevel ?? ""), actions]);
    }
  };
  unsubs.push(onValue(me, s => {
    const lv = s.val()?.level ?? 0;
    const lb = s.val()?.logoutBefore; // 다른 곳에서 "모두 로그아웃" / 관리자 강제 로그아웃
    if (lb && lb > loginAt && !relogging) { logout("로그아웃되었습니다 (다른 곳에서 '모두 로그아웃'했거나 관리자가 로그아웃시킴). 다시 로그인하세요."); return; }
    blocked = !!s.val()?.blocked;
    $("me").textContent = `${name} (레벨 ${lv})`;
    admin = lv === 9;
    if (admin && $("admin").hidden) startAdmin();
    $("admin").hidden = !admin;
    level = lv;
    watchPcs(lv >= 1);
    render();
  }));
  // PC 목록은 레벨 1 이상만 (규칙도 같음): 레벨이 바뀌면 구독을 켜고 끔
  let pcsOff = null;
  const watchPcs = on => {
    if (on && !pcsOff) pcsOff = onValue(ref(db, "pcs"), s => { pcs = s.val() || {}; render(); }, () => { pcs = {}; render(); });
    if (!on && pcsOff) { pcsOff(); pcsOff = null; pcs = {}; }
  };
  unsubs.push(() => watchPcs(false));
  const t = setInterval(render, 15000); unsubs.push(() => clearInterval(t));
});

// ---------- 원격 접속 ----------
const video = $("video");
let pc = null, dc = null;

// 영상·소리를 서로 다른 스트림으로 표시 → Chrome이 립싱크한다고 영상을 소리에 맞춰 늦추지 않음 (원격 조작은 영상 지연이 치명적)
function ownStreams(sdp) {
  return sdp.split(/\r\n(?=m=)/).map(sec => {
    const kind = /^m=(audio|video)/.exec(sec)?.[1];
    if (!kind) return sec;
    return sec.replace(/\r\na=msid:[^\r\n]*/g, "").replace(/\r\na=ssrc:(\d+) msid:[^\r\n]*/g, "").replace(/\r\n$/, "") + `\r\na=msid:ht-${kind} ht-${kind}-track\r\n`;
  }).join("\r\n").replace(/\r\n\r\n/g, "\r\n");
}

// 요청 작성 → 에이전트 승인 대기 → 에이전트의 offer에 answer. setup(conn)에서 이벤트 연결. 실패 시 throw.
async function openSession(pcId, mode, code, setup) {
  const reqRef = push(ref(db, `requests/${pcId}`));
  const req = { uid: auth.currentUser.uid, mode, status: "pending", createdAt: serverTimestamp() };
  if (code) req.code = code;
  await set(reqRef, req); // DB 규칙이 레벨/코드/게스트 허용/관리자 여부를 검사 → 실패 시 PERMISSION_DENIED

  const wait = (await get(ref(db, "settings/approvalTimeout")).catch(() => null))?.val() || 30;
  const result = await new Promise(res => {
    const r = ref(db, `requests/${pcId}/${reqRef.key}/status`);
    // 에이전트가 settings/approvalTimeout초 뒤 "timeout"으로 취소함. 이 타이머는 PC가 꺼져 있을 때용 (승인 시간 + 30초).
    const timer = setTimeout(() => { off(r); res("offline"); }, (wait + 30) * 1000);
    onValue(r, s => { if (s.val() && s.val() !== "pending") { clearTimeout(timer); off(r); res(s.val()); } }, () => res("denied"));
  });
  if (result !== "approved") throw new Error(result === "offline" ? "응답이 없습니다 (PC가 오프라인일 수 있음)."
    : result === "timeout" ? `${wait}초 안에 승인되지 않아 요청이 취소되었습니다.` : "접속이 거부되었습니다.");

  const sRef = `sessions/${pcId}/${reqRef.key}`;
  const s = (await get(ref(db, sRef))).val();
  const conn = new RTCPeerConnection({ iceServers: s.iceServers });
  setup(conn);
  await conn.setRemoteDescription({ type: "offer", sdp: ownStreams(s.offer.sdp) });
  const candRef = ref(db, `${sRef}/callerCandidates`);
  onChildAdded(candRef, c => {
    const v = c.val();
    if (!v.candidate.startsWith("candidate:")) v.candidate = "candidate:" + v.candidate;
    conn.addIceCandidate(v).catch(() => {});
  });
  conn.addEventListener("connectionstatechange", () => conn.connectionState === "closed" && off(candRef));
  await conn.setLocalDescription(await conn.createAnswer());
  // 비-trickle: 후보 수집이 끝난 answer SDP를 한 번에 전달
  await new Promise(res => {
    if (conn.iceGatheringState === "complete") return res();
    conn.onicegatheringstatechange = () => conn.iceGatheringState === "complete" && res();
    setTimeout(res, 3000);
  });
  await set(ref(db, `${sRef}/answer`), { type: "answer", sdp: conn.localDescription.sdp });
  return conn;
}

let heartbeat = null;
let session = null; // 지금 연결한 PC (재부팅 후 다시 연결용)
async function connect(pcId, mode, code) {
  session = { pcId, mode };
  status(mode === "guest" ? "원격 PC의 승인을 기다리는 중..." : "연결 대기 중...");
  let connected;
  const up = new Promise((res, rej) => connected = { res, rej });
  up.catch(() => {}); // 시간 초과 후 늦게 실패해도 무시
  pc = await openSession(pcId, mode, code, c => {
    c.ontrack = e => {
      if (e.track.kind === "audio") { audioEl.srcObject = new MediaStream([e.track]); audioEl.play().catch(() => {}); return; } // 원격 PC 소리
      // 지연 최소: 지터 버퍼 목표 0 (Chrome 114+ jitterBufferTarget, 이전 playoutDelayHint)
      try { e.receiver.jitterBufferTarget = 0; } catch { }
      try { e.receiver.playoutDelayHint = 0; } catch { }
      video.srcObject = new MediaStream([e.track]); video.play().catch(() => {});
    };
    c.ondatachannel = e => {
      console.log("[remote] 채널", e.channel.label);
      if (e.channel.label === "file") { fileCh = e.channel; fileCh.binaryType = "arraybuffer"; fileCh.onmessage = onFileMessage; return; }
      dc = e.channel;
      dc.onmessage = ev => onAgentMessage(JSON.parse(ev.data));
    };
    c.oniceconnectionstatechange = () => console.log("[remote] ice", c.iceConnectionState);
    let wasUp = false;
    c.onconnectionstatechange = () => {
      console.log("[remote] connection", c.connectionState);
      status("연결 상태: " + c.connectionState);
      if (c.connectionState === "connected") { wasUp = true; connected.res(); }
      if (["failed", "closed", "disconnected"].includes(c.connectionState)) {
        if (wasUp) hangUp(); // 연결 중 실패는 아래 catch가 원인을 모은 뒤 정리
        else connected.rej(new Error(`P2P 연결 실패 (${c.connectionState})`));
      }
    };
  });
  console.log("[remote] answer 후보:", pc.localDescription.sdp.split("\n").filter(l => l.startsWith("a=candidate")));
  status("P2P 연결 중...");
  try {
    await Promise.race([up, new Promise((_, rej) => setTimeout(() => rej(new Error("20초 안에 P2P 연결이 안 됨")), 20000))]);
  } catch (e) {
    const why = await iceDiag(pc);
    console.log("[remote] 실패 진단:", why);
    hangUp();
    throw new Error(`${e.message} — ${why}`);
  }
  // 에이전트는 10초간 아무 메시지도 없으면 세션 종료 (탭 강제 종료·브라우저 충돌 대비)
  heartbeat = setInterval(() => send({ t: "hb" }), 2000);
  $("viewer").style.display = "block";
  video.focus();
}

// 연결 실패 원인 표시: 양쪽이 어떤 경로를 내놨는지 + TURN(중계) 유무.
// host=같은 망, srflx/prflx=공인 IP(NAT 통과), relay=TURN 중계. LTE/5G(통신사 NAT)는 대개 relay가 있어야 붙음.
async function iceDiag(c) {
  const kind = { host: "같은 망", srflx: "공인 IP", prflx: "공인 IP", relay: "TURN 중계" };
  const loc = new Set(), rem = new Set();
  try { (await c.getStats()).forEach(s => { if (s.type === "local-candidate") loc.add(s.candidateType); if (s.type === "remote-candidate") rem.add(s.candidateType); }); } catch { }
  const names = s => [...s].map(t => kind[t] || t).join("·") || "없음";
  const hasTurn = (c.getConfiguration().iceServers || []).some(s => [].concat(s.urls).some(u => /^turns?:/.test(u)));
  return `이 기기 [${names(loc)}] ↔ 원격 PC [${names(rem)}]` +
    (hasTurn ? "" : " · TURN 중계 서버가 설정되지 않음 → 모바일 데이터(LTE/5G)·다른 회사망에서는 연결이 안 될 수 있음");
}

// 에이전트 → 뷰어: 커서 모양 (내 마우스 포인터에 적용, 지연 없음), 모니터 목록 (2개 이상이면 전환 버튼)
function onAgentMessage(m) {
  if (m.t === "cur") for (const v of allVideos()) v.style.cursor = m.c;
  if (m.t === "mons") { mons = m; renderMons(); relayout(); }
  if (m.t === "bye") { hangUp(); status(m.m); } // 에이전트가 거부 (예: 이 PC 자신에게 접속)
  if (m.t === "caps") renderTools(m);
  if (m.t === "msg") xfer(m.m);
  if (m.t === "clip") { lastClip = m.s; pullClip = m.s; writeClip(); } // 원격에서 복사 → 내 클립보드
}

// ---------- 도구 막대: 소리, 파일 받기, Ctrl+Alt+Del, (관리자) 화면 가리기·입력 잠금·재부팅 ----------
const audioEl = $("audio");
const tool = (text, click, title = "") => el("button", { textContent: text, title }, { click: e => { click(e.currentTarget); video.focus(); } });
const toggle = (b, on, text) => { b.classList.toggle("on", on); b.textContent = text; };
function renderTools(caps) {
  const t = [
    tool(audioEl.muted ? "🔇 소리" : "🔊 소리", b => { audioEl.muted = !audioEl.muted; b.textContent = audioEl.muted ? "🔇 소리" : "🔊 소리"; audioEl.play().catch(() => {}); }, "원격 PC 소리 켜기/끄기"),
    tool("📥 파일 받기", () => { send({ t: "getfile" }); xfer("원격 화면에 뜬 창에서 파일을 고르세요"); }, "원격 PC의 파일을 이 기기로 다운로드"),
  ];
  if (caps.service) t.push(tool("Ctrl+Alt+Del", () => send({ t: "cad" })));
  if (caps.admin) { // 관리자 접속만: 기본은 둘 다 꺼짐, 따로따로
    let blank = false, lock = false;
    t.push(
      tool("화면 가리기", b => { blank = !blank; toggle(b, blank, blank ? "화면 가림 ✓" : "화면 가리기"); send({ t: "blank", on: blank }); }, "원격 PC 앞 모니터를 검게 (이 화면에는 그대로 보임)"),
      tool("입력 잠금", b => { lock = !lock; toggle(b, lock, lock ? "입력 잠금 ✓" : "입력 잠금"); send({ t: "lock", on: lock }); }, "원격 PC 앞 키보드·마우스 막기"),
      tool("재부팅", reboot, "원격 PC를 다시 시작하고, 켜지면 자동으로 다시 연결"));
  }
  $("tools").replaceChildren(...t);
}

// 클립보드 자동 동기화 (글자만): 어느 쪽에서 복사하든 서로의 클립보드에 들어감.
//  원격 → 나: 에이전트가 바뀔 때마다 보냄. 브라우저는 이 창이 활성일 때만 쓰기를 허용 → 아니면 돌아올 때 씀.
//  나 → 원격: 브라우저는 클립보드 변경을 알려 주지 않음 → 이 창이 활성인 동안 1초마다 읽어 비교 + 창으로 돌아올 때 + Ctrl+V 직전.
//  (읽기 권한을 물어볼 수 없는 브라우저 — Firefox·iOS Safari — 는 매번 "붙여넣기" 확인이 떠서 Ctrl+V 때만 읽음)
let lastClip = null, pullClip = null, clipPoll = false;
async function pushClip() {
  if (!pc || !document.hasFocus() || pullClip !== null) return; // 원격에서 온 글자를 아직 못 썼으면 옛 글자로 덮어쓰지 않게
  try {
    const s = await navigator.clipboard.readText();
    if (pullClip !== null) return; // 읽는 사이 원격에서 새 글자가 옴 → 방금 읽은 건 옛 글자
    if (s && s.length <= 100000 && s !== lastClip) { lastClip = s; send({ t: "clip", s }); }
  } catch { }
}
function writeClip() {
  if (pullClip === null || !document.hasFocus()) return;
  const s = pullClip;
  // 실패해도 이 창이 활성이면 다시 시도하지 않음 (계속 남아 있으면 내 PC → 원격 방향이 영영 막힘)
  navigator.clipboard?.writeText(s).then(() => { if (pullClip === s) pullClip = null; },
    e => { console.log("[remote] 클립보드 쓰기 실패", e); if (pullClip === s && document.hasFocus()) { pullClip = null; xfer("원격에서 복사한 글자를 이 PC 클립보드에 넣지 못했습니다 (브라우저 권한 확인)"); } });
}
navigator.permissions?.query({ name: "clipboard-read" }).then(p => clipPoll = p.state !== "denied", () => {});
setInterval(() => clipPoll && pushClip(), 1000);
addEventListener("focus", () => { writeClip(); pushClip(); });

// 재부팅 → 연결 종료 → PC가 다시 온라인이 되면 자동으로 다시 연결 (최대 10분)
const sleep = ms => new Promise(r => setTimeout(r, ms));
async function reboot() {
  if (!confirm("원격 PC를 재부팅할까요?\n저장하지 않은 작업은 사라집니다.")) return;
  const s = session, t0 = Date.now() + serverOffset;
  send({ t: "reboot" });
  await sleep(1500);
  hangUp();
  status("원격 PC 재부팅 중... 다시 켜지면 자동으로 연결합니다");
  for (let i = 0; i < 120; i++) {
    await sleep(5000);
    if (pc) return; // 그사이 직접 연결함
    const p = (await get(ref(db, `pcs/${s.pcId}`)).catch(() => null))?.val();
    if (p?.online && p.lastSeen > t0 + 15000) return connect(s.pcId, s.mode).catch(fail);
  }
  status("재부팅 후 10분 안에 PC가 다시 연결되지 않았습니다.");
}

// ---------- 모니터: 하나만 보기(기본, 원격 PC 부담 적음) / 모든 모니터(창 여러 개, 원격 PC 부담 약 2배) ----------
// 모든 모니터 = 에이전트가 가상 화면 전체를 영상 하나로 보냄({t:"mon",i:-1}) → 이 창과 새 창들이 각자 한 모니터 부분만 잘라 보여 줌.
// 입력 좌표는 영상 전체 기준이라 에이전트 쪽 변환이 그대로 맞음.
let mons = null;          // 마지막 {t:"mons"}: cur(-1=모두), vx/vy/vw/vh(전체 화면), list[{i,x,y,w,h,primary}]
let mainMon = 0;          // 모든 모니터일 때 이 창이 보여 줄 모니터
const popups = new Map(); // 모니터 번호 → { win, video }
const allMode = () => mons?.cur === -1;
const allVideos = () => [video, ...[...popups.values()].map(p => p.video)];

function renderMons() {
  const all = allMode();
  const btns = mons.list.length < 2 ? [] : mons.list.map(s =>
    el("button", { textContent: `모니터 ${s.i + 1}${s.primary ? " (주)" : ""} ${s.w}×${s.h}`, className: !all && s.i === mons.cur ? "cur" : "" },
      { click: () => { showMon(s.i); video.focus(); } }));
  // 휴대폰은 창을 여러 개 못 띄움. vw가 없으면 "모든 모니터"를 모르는 옛 에이전트.
  if (btns.length && !isTouch && mons.vw) btns.push(el("button", { textContent: "모든 모니터 (창 여러 개)", className: all ? "cur" : "" }, { click: openAll }));
  $("mons").replaceChildren(...btns);
}

function showMon(i) { closePopups(); send({ t: "mon", i }); }

// 원격 전체 화면 중 모니터 s 부분만 부모 상자에 꽉 차게(비율 유지) 보이도록 video 위치·크기 지정. s 없으면 원래대로.
function crop(v, s) {
  const st = v.style;
  if (!s || !mons) { st.left = st.top = st.width = st.height = ""; return; }
  const box = v.parentElement.getBoundingClientRect(), k = Math.min(box.width / s.w, box.height / s.h);
  Object.assign(st, {
    width: mons.vw * k + "px", height: mons.vh * k + "px",
    left: (box.width - s.w * k) / 2 - (s.x - mons.vx) * k + "px", top: (box.height - s.h * k) / 2 - (s.y - mons.vy) * k + "px",
  });
}
function relayout() {
  crop(video, allMode() ? mons.list[mainMon] : null);
  for (const [i, p] of popups) crop(p.video, allMode() ? mons.list[i] : null);
}
addEventListener("resize", relayout);

// 브라우저는 클릭 한 번에 새 창을 하나만 열어 줌 → 모니터 3개 이상이면 다시 누를 때마다 남은 창을 하나씩 엶
function openAll() {
  if (!allMode()) { if (mons.cur >= 0) mainMon = mons.cur; closePopups(); } // 지금 보던 모니터는 이 창에, 나머지는 새 창에
  const missing = mons.list.filter(s => s.i !== mainMon && !popups.has(s.i));
  let opened = 0;
  for (const s of missing) if (openPopup(s)) opened++; else break;
  if (!popups.size) return xfer("새 창이 차단되었습니다. 주소창 오른쪽의 팝업 차단 아이콘에서 허용한 뒤 다시 누르세요.");
  if (opened < missing.length) xfer(`"모든 모니터"를 한 번 더 누르면 나머지 창이 열립니다 (${missing.length - opened}개 남음)`);
  if (!allMode()) send({ t: "mon", i: -1 });
  placePopups();
}

function openPopup(s) {
  const w = open("", "htutil-mon" + s.i, "popup,width=960,height=600");
  if (!w) return false;
  const d = w.document;
  d.title = `HtUtil · 모니터 ${s.i + 1}`;
  d.body.replaceChildren();
  d.body.style.cssText = "margin:0;background:#000;overflow:hidden";
  const box = d.createElement("div"), v = d.createElement("video");
  box.style.cssText = "position:fixed;inset:0;overflow:hidden";
  v.style.cssText = "position:absolute;left:0;top:0;width:100%;height:100%;object-fit:contain;outline:none";
  v.autoplay = v.muted = v.playsInline = true; v.tabIndex = 0;
  v.srcObject = video.srcObject; v.play().catch(() => {});
  box.append(v); d.body.append(box);
  bindInput(v);
  w.addEventListener("resize", relayout);
  // 새 창을 닫으면 하나만 보기로 돌아감 (원격 PC도 그 모니터만 캡처)
  w.addEventListener("pagehide", () => { if (popups.get(s.i)?.win === w) { closePopups(); if (allMode()) showMon(mainMon); } });
  popups.set(s.i, { win: w, video: v });
  return true;
}

function closePopups() {
  const ws = [...popups.values()].map(p => p.win);
  popups.clear(); // 먼저 비워서 각 창의 pagehide가 다시 전환하지 않게 함
  for (const w of ws) try { w.close(); } catch { }
}

// Chrome: 새 창을 내 PC의 다른 모니터로 옮겨 그 화면 크기로 (처음 한 번 "창 관리" 권한을 물음). 안 되면 직접 옮기면 됨.
async function placePopups() {
  try {
    const sd = await window.getScreenDetails?.();
    if (!sd) return;
    const others = sd.screens.filter(x => x !== sd.currentScreen);
    [...popups.values()].forEach(({ win }, n) => {
      const sc = others[n];
      if (sc) { win.moveTo(sc.availLeft, sc.availTop); win.resizeTo(sc.availWidth, sc.availHeight); }
    });
  } catch { }
}

// ---------- 파일 보내기: 원격 화면에 드래그앤드롭 → "file" 채널로 한 개씩 전송 → 원격 PC 바탕화면에 저장 ----------
let fileCh = null, fileQueue = Promise.resolve();
const xfer = s => $("xfer").textContent = s;
function sendFile(f) {
  return new Promise((resolve, reject) => {
    const ch = fileCh;
    if (ch?.readyState !== "open") return reject(new Error("파일 채널이 열려 있지 않음"));
    pendingSend = { resolve, reject };
    ch.bufferedAmountLowThreshold = 256 * 1024;
    (async () => {
      ch.send(JSON.stringify({ t: "file", name: f.name, size: f.size }));
      const CHUNK = 16384;
      for (let o = 0; o < f.size; o += CHUNK) {
        if (ch.readyState !== "open") return reject(new Error("연결이 끊겼습니다"));
        if (ch.bufferedAmount > 1024 * 1024) await new Promise(r => ch.onbufferedamountlow = r);
        ch.send(await f.slice(o, o + CHUNK).arrayBuffer());
        xfer(`보내는 중: ${f.name} ${Math.floor(o * 100 / f.size)}%`);
      }
      ch.send(JSON.stringify({ t: "end" }));
      xfer(`저장 중: ${f.name}`);
    })().catch(reject);
  });
}
// 원격 PC → 이 기기: {t:"file",name,size} → 조각들 → {t:"end"} → 브라우저 다운로드
let pendingSend = null, inFile = null;
function onFileMessage(e) {
  if (typeof e.data !== "string") {
    if (!inFile) return;
    inFile.parts.push(e.data); inFile.got += e.data.byteLength;
    const pct = Math.floor(inFile.got * 100 / Math.max(1, inFile.size));
    if (pct !== inFile.pct) xfer(`받는 중: ${inFile.name} ${inFile.pct = pct}%`);
    return;
  }
  const m = JSON.parse(e.data);
  if (m.t === "file") inFile = { name: m.name, size: m.size, parts: [], got: 0, pct: -1 };
  else if (m.t === "end" && inFile) {
    const a = el("a", { href: URL.createObjectURL(new Blob(inFile.parts)), download: inFile.name });
    a.click(); setTimeout(() => URL.revokeObjectURL(a.href), 60000);
    xfer(`받음: ${inFile.name} (다운로드 폴더)`); inFile = null;
  }
  else if (m.t === "saved") pendingSend?.resolve(m.name);
  else if (m.t === "error") pendingSend?.reject(new Error(m.msg));
}
const viewerEl = $("viewer");
viewerEl.addEventListener("dragover", e => { e.preventDefault(); $("drop").style.display = "flex"; });
viewerEl.addEventListener("dragleave", e => { if (e.target === viewerEl || !viewerEl.contains(e.relatedTarget)) $("drop").style.display = "none"; });
viewerEl.addEventListener("drop", e => {
  e.preventDefault(); $("drop").style.display = "none";
  // 한 번 놓은 파일들 = 한 묶음: 에이전트가 바탕화면에 저장 + 원격 클립보드에 "잘라내기"로 넣음 → 원격 폴더에서 Ctrl+V = 그 폴더로 이동
  fileQueue = fileQueue.then(() => fileCh?.readyState === "open" && fileCh.send(JSON.stringify({ t: "batch" })));
  for (const f of e.dataTransfer.files) // 폴더는 지원 안 함 (파일만)
    fileQueue = fileQueue.then(() => sendFile(f)).then(name => xfer(`완료: ${name} → 원격 바탕화면 (원격에서 원하는 폴더를 열고 Ctrl+V 하면 그 폴더로 옮겨짐)`), err => xfer(`실패: ${f.name} — ${err.message}`));
  video.focus();
});

function hangUp() {
  clearInterval(heartbeat);
  if (!pc) return;
  pc.close(); pc = null; dc = null; fileCh = null; inFile = null;
  audioEl.srcObject = null; $("tools").replaceChildren();
  closePopups(); mons = null; crop(video, null);
  video.srcObject = null; video.style.cursor = ""; $("mons").replaceChildren();
  $("viewer").style.display = "none";
  status("연결 종료");
}
$("hangUp").onclick = hangUp;
// 탭 닫기·새로고침·이동 시 즉시 연결 종료 (에이전트가 바로 감지)
addEventListener("pagehide", hangUp);

// ---------- 입력 전송 (DataChannel) ----------
const send = m => dc?.readyState === "open" && dc.send(JSON.stringify(m));
// 영상 전체 기준 0..1 좌표 ("모든 모니터"로 잘라 보일 때도 video 요소는 전체 화면 크기라 그대로 맞음)
function pos(e, v = video) {
  const r = v.getBoundingClientRect(), vw = v.videoWidth, vh = v.videoHeight;
  if (!vw) return null;
  const sc = Math.min(r.width / vw, r.height / vh);
  const clamp = n => Math.max(0, Math.min(1, n));
  return { x: clamp((e.clientX - r.left - (r.width - vw * sc) / 2) / (vw * sc)),
           y: clamp((e.clientY - r.top - (r.height - vh * sc) / 2) / (vh * sc)) };
}
// 마우스·키보드 (이 창의 video + "모든 모니터" 새 창들의 video)
function bindInput(v) {
  v.addEventListener("mousemove", e => { const p = pos(e, v); p && send({ t: "mm", ...p }); });
  v.addEventListener("mousedown", e => { v.focus(); send({ t: "md", b: e.button }); e.preventDefault(); });
  v.addEventListener("mouseup", e => send({ t: "mu", b: e.button }));
  v.addEventListener("contextmenu", e => e.preventDefault());
  v.addEventListener("wheel", e => { send({ t: "wh", d: Math.sign(e.deltaY) * 120 }); e.preventDefault(); }, { passive: false });
  // Ctrl+V: 내 클립보드를 먼저 원격으로 보낸 뒤 V를 눌러 원격에서 바로 붙여넣기 되게
  let clipWait = null;
  v.addEventListener("keydown", e => {
    e.preventDefault();
    if ((e.ctrlKey || e.metaKey) && e.keyCode === 86 && !e.repeat) { clipWait = pushClip().finally(() => send({ t: "kd", k: 86 })); return; }
    send({ t: "kd", k: e.keyCode });
  });
  v.addEventListener("keyup", e => {
    e.preventDefault();
    if (e.keyCode === 86 && clipWait) { const w = clipWait; clipWait = null; return w.then(() => send({ t: "ku", k: 86 })); }
    send({ t: "ku", k: e.keyCode });
  });
}
bindInput(video);

// ---------- 터치 (모바일): 탭=클릭, 길게 누름=우클릭, 끌기=드래그 (스크롤 모드면 휠), 두 손가락=브라우저 확대/이동 ----------
// 화면을 확대하면 한 손가락 끌기는 브라우저가 확대 화면 이동으로 가져감 → "탭 후 바로 다시 눌러 끌기"는 항상 원격 드래그
// (탭 직후 잠깐 touch-action:none으로 바꿔 다음 터치를 브라우저가 못 가져가게 함).
// touch의 pointerdown에서 preventDefault → 브라우저가 흉내 내는 mousedown/up이 안 생겨 위 마우스 처리와 겹치지 않음
const T = { id: null, x0: 0, y0: 0, wy: 0, moved: false, long: false, drag: false, timer: 0, fingers: 0 };
let scrollMode = false;
const moveTo = (x, y) => { const p = pos({ clientX: x, clientY: y }); p && send({ t: "mm", ...p }); };
const clickBtn = b => { send({ t: "md", b }); send({ t: "mu", b }); };
const TAP_DRAG_MS = 350;
let lastTap = { t: 0, x: 0, y: 0 }, armTimer = 0;
const holdTouch = on => { clearTimeout(armTimer); video.style.touchAction = on ? "none" : ""; };
function armTapDrag(x, y) { // 방금 탭한 자리를 곧 다시 누르면 원격 드래그
  lastTap = { t: performance.now(), x, y };
  holdTouch(true);
  armTimer = setTimeout(() => holdTouch(false), TAP_DRAG_MS);
}
const dragging = () => T.drag || !scrollMode; // 끌기가 원격 드래그인지 (아니면 휠)
// iOS는 touch-action 변경을 늦게 반영할 수 있어 터치 시작도 직접 막음
video.addEventListener("touchstart", e => { if (video.style.touchAction === "none" && e.touches.length === 1) e.preventDefault(); }, { passive: false });
function cancelTouch() {
  clearTimeout(T.timer);
  if (T.id !== null && T.moved && dragging() && !T.long) send({ t: "mu", b: 0 }); // 드래그 중이었으면 놓기
  if (T.drag) holdTouch(false);
  T.id = null; T.drag = false;
}
video.addEventListener("pointerdown", e => {
  if (e.pointerType !== "touch") return;
  e.preventDefault();
  if (++T.fingers > 1) return cancelTouch(); // 두 번째 손가락 = 확대/이동 제스처
  const drag = performance.now() - lastTap.t < TAP_DRAG_MS && Math.hypot(e.clientX - lastTap.x, e.clientY - lastTap.y) < 40;
  Object.assign(T, { id: e.pointerId, x0: e.clientX, y0: e.clientY, wy: e.clientY, moved: false, long: false, drag });
  if (drag) holdTouch(true); // 손을 뗄 때까지 브라우저 이동 금지, 길게 눌러도 우클릭 안 함 (천천히 끌 수 있게)
  else T.timer = setTimeout(() => { T.long = true; moveTo(T.x0, T.y0); clickBtn(2); navigator.vibrate?.(30); }, 550);
});
video.addEventListener("pointermove", e => {
  if (e.pointerType !== "touch" || e.pointerId !== T.id || T.long) return;
  if (!T.moved) {
    if (Math.hypot(e.clientX - T.x0, e.clientY - T.y0) < 10) return; // 손떨림은 탭으로 취급
    T.moved = true; clearTimeout(T.timer);
    if (dragging()) { moveTo(T.x0, T.y0); send({ t: "md", b: 0 }); }
  }
  if (dragging()) return moveTo(e.clientX, e.clientY);
  const d = T.wy - e.clientY; // 손가락 위로 = 아래로 스크롤
  if (Math.abs(d) >= 30) { send({ t: "wh", d: Math.sign(d) * 120 }); T.wy = e.clientY; }
});
video.addEventListener("pointerup", e => {
  if (e.pointerType !== "touch") return;
  T.fingers = Math.max(0, T.fingers - 1);
  if (e.pointerId !== T.id) return;
  clearTimeout(T.timer); T.id = null;
  const drag = T.drag; T.drag = false;
  if (drag) holdTouch(false);
  if (T.long) return;                                   // 이미 우클릭함
  if (T.moved) { if (drag || !scrollMode) send({ t: "mu", b: 0 }); return; }
  moveTo(e.clientX, e.clientY); clickBtn(0);            // 탭 (빠르게 두 번 = 더블클릭)
  armTapDrag(e.clientX, e.clientY);
});
video.addEventListener("pointercancel", e => { if (e.pointerType === "touch") { T.fingers = Math.max(0, T.fingers - 1); cancelTouch(); } });

// ---------- 모바일 키보드: 화면 밖 입력칸을 원격과 "똑같이" 맞춤 (키 코드는 한글 조합 중 229라 못 씀) ----------
// 입력칸을 매번 비우면 안 됨: 한글 자판은 칸에 남은 앞 글자를 보고 받침/다음 글자를 정함 (비우면 "안녕"→"안녀ㅇ").
// 그래서 칸 내용은 그대로 두고, 바뀔 때마다 이전 내용과 비교해 지워진 만큼 Backspace + 새 글자만 보냄.
// 맨 앞 공백(SENTINEL)은 칸이 비었을 때도 ⌫가 입력 이벤트로 오게 하려는 것 (지워지면 = 원격 Backspace).
const kbdin = $("kbdin"), SENTINEL = " ", mods = new Set();
let kbdShadow = SENTINEL; // 마지막으로 원격에 반영된 입력칸 내용
kbdin.value = SENTINEL;
/** 입력칸 변화 → 원격에 보낼 것: dels개 지우고 ins 입력 (공통 앞부분은 그대로) */
function kbdDiff(oldV, newV) {
  const a = [...oldV], b = [...newV]; // 코드포인트 단위 (이모지 등)
  let p = 0;
  while (p < a.length && p < b.length && a[p] === b[p]) p++;
  return { dels: a.length - p, ins: b.slice(p).join("") };
}
const modButtons = () => document.querySelectorAll("#kbdbar [data-mod]").forEach(b => b.classList.toggle("on", mods.has(+b.dataset.mod)));
function tapKey(vk) { // 켜 둔 Ctrl/Alt/Shift/Win과 함께 한 번 누르고 수식키는 해제
  for (const m of mods) send({ t: "kd", k: m });
  send({ t: "kd", k: vk }); send({ t: "ku", k: vk });
  for (const m of mods) send({ t: "ku", k: m });
  mods.clear(); modButtons();
}
function typeText(s) {
  if (!s) return;
  if (!mods.size) return send({ t: "txt", s });
  for (const ch of s.toUpperCase()) if (/^[A-Z0-9]$/.test(ch)) tapKey(ch.charCodeAt(0)); // Ctrl+C 등은 키로 보내야 함
}
function resetKbd() { kbdin.value = kbdShadow = SENTINEL; } // 조합 중이 아닐 때만 (조합 중에 값을 바꾸면 자판이 꼬임)
function syncKbd(composing) {
  const v = kbdin.value, { dels, ins } = kbdDiff(kbdShadow, v);
  for (let i = 0; i < dels; i++) tapKey(8); // 조합 중 글자 바뀜("녀"→"녕")도 ⌫ + 새 글자로 반영
  typeText(ins);
  kbdShadow = v;
  if (composing) return;
  if (!v.startsWith(SENTINEL)) resetKbd();                    // 맨 앞 공백까지 지워짐 → 다시 채움
  else if (v.length > 40 && /\s$/.test(v)) resetKbd();        // 너무 길어지면 단어 경계에서 비움
}
kbdin.addEventListener("input", e => syncKbd(e.isComposing));
kbdin.addEventListener("compositionend", () => setTimeout(() => syncKbd(false)));
kbdin.addEventListener("keydown", e => {
  if (e.isComposing || e.keyCode === 229) return;
  const vk = { Enter: 13, Tab: 9, Escape: 27, ArrowLeft: 37, ArrowUp: 38, ArrowRight: 39, ArrowDown: 40, Delete: 46 }[e.key];
  if (!vk) return;
  e.preventDefault(); tapKey(vk);
  if (vk === 13) resetKbd(); // 줄이 바뀌면 앞 글자와 조합될 일 없음
});
const kbdbar = $("kbdbar");
kbdbar.addEventListener("pointerdown", e => e.preventDefault()); // 버튼을 눌러도 키보드(입력칸 포커스)가 닫히지 않게
kbdbar.addEventListener("click", e => {
  const b = e.target.closest("button"); if (!b) return;
  if (b.dataset.act === "kbd") kbdin.focus();
  else if (b.dataset.act === "scroll") b.classList.toggle("on", scrollMode = !scrollMode);
  else if (b.dataset.mod) { const m = +b.dataset.mod; mods.has(m) ? mods.delete(m) : mods.add(m); modButtons(); }
  else if (b.dataset.vk) tapKey(+b.dataset.vk);
});
const isTouch = matchMedia("(pointer: coarse)").matches;
kbdbar.hidden = !isTouch;
$("viewer").classList.toggle("touch", isTouch);

// ---------- 관리자 (레벨 9) — 실제 차단은 DB 규칙에서 ----------
const ipKey = ip => (ip || "").replaceAll(".", "_"); // RTDB 키에 '.' 사용 불가

function renamePc(id, current) {
  const name = prompt(`PC ${id}의 새 이름`, current || "")?.trim();
  if (!name || name === current) return;
  if (name.length > 100) return status("이름은 100자 이하로 입력하세요.");
  update(ref(db, `pcs/${id}`), { name }).then(() => status(`이름 변경됨: ${name}`), fail);
}
// 삭제된 PC의 에이전트는 다음 실행 때 새 PC ID로 다시 등록됨.
// 관리자: 지금 원격 중인 세션을 끊고 그 계정의 원격 접속을 차단 (사용자 표에서 "차단 해제"로 다시 허용)
async function kickPc(id, p) {
  const r = p.remote, guest = !r.uid;
  if (r.uid === auth.currentUser.uid) return status("내 원격은 원격 화면의 \"연결 종료\"로 끝내세요.");
  if (!confirm(`${r.who} 님의 원격을 강제로 해제할까요?` + (guest ? "\n(게스트: 코드가 새로 바뀝니다)" : "\n이 계정은 관리자가 허용할 때까지 원격 접속이 차단됩니다."))) return;
  try {
    if (!guest) await set(ref(db, `users/${r.uid}/blocked`), true);
    await set(push(ref(db, `requests/${id}`)), { uid: auth.currentUser.uid, mode: "kick", status: "pending", createdAt: serverTimestamp() });
    status(`${r.who} 님의 원격을 해제했습니다` + (guest ? "" : " (계정 차단됨)"));
  } catch (e) { fail(e); }
}

function deletePc(id, p) {
  if (!confirm(`PC "${p.name || id}" (${id})를 삭제할까요?\n에이전트가 다시 실행되면 새 PC ID로 다시 등록됩니다.`)) return;
  const upd = { [`pcs/${id}`]: null, [`pcSecrets/${id}`]: null };
  if (p.agentUid) Object.assign(upd, { [`agents/${p.agentUid}`]: null, [`agentInfo/${p.agentUid}`]: null });
  update(ref(db), upd).then(() => status(`PC ${p.name || id} 삭제됨`), fail);
}

// ---------- 에이전트 업데이트: 파일은 저장하지 않고 관리자 브라우저 → 각 에이전트로 직접 전송 (DataChannel) ----------
// exe 버전 = Windows가 읽는 것과 같은 값: PE 리소스 트리의 RT_VERSION(16) 항목 → VS_FIXEDFILEINFO(서명 0xFEEF04BD).
// 바이트를 그냥 훑으면 안 됨: self-contained exe의 .rsrc에는 트리에서 끊긴 .NET 런타임 버전(9.0.x) 데이터가 앞에 남아 있음.
function exeVersion(buf) {
  try {
    const dv = new DataView(buf), u16 = o => dv.getUint16(o, true), u32 = o => dv.getUint32(o, true);
    if (u16(0) !== 0x5A4D) return null; // "MZ"
    const pe = u32(0x3C);
    if (u32(pe) !== 0x4550) return null; // "PE\0\0"
    const nSec = u16(pe + 6), secTable = pe + 24 + u16(pe + 20);
    let raw = -1, va = 0;
    for (let s = 0; s < nSec; s++) {
      const o = secTable + s * 40;
      if (String.fromCharCode(...new Uint8Array(buf, o, 8)).replace(/\0+$/, "") === ".rsrc") { va = u32(o + 12); raw = u32(o + 20); }
    }
    if (raw < 0) return null;
    // 리소스 디렉터리: 항목 = { id, ptr } (ptr 최상위 비트 = 하위 디렉터리)
    const entries = off => Array.from({ length: u16(raw + off + 12) + u16(raw + off + 14) }, (_, k) => ({ id: u32(raw + off + 16 + k * 8), ptr: u32(raw + off + 20 + k * 8) }));
    const type = entries(0).find(e => e.id === 16); // RT_VERSION
    if (!type) return null;
    const name = entries(type.ptr & 0x7fffffff)[0], lang = entries(name.ptr & 0x7fffffff)[0];
    const data = raw + lang.ptr, start = u32(data) - va + raw, end = start + u32(data + 4);
    for (let i = start; i + 16 <= end; i += 4)
      if (u32(i) === 0xFEEF04BD) { const ms = u32(i + 8), ls = u32(i + 12); return [ms >>> 16, ms & 0xffff, ls >>> 16, ls & 0xffff]; }
  } catch { } // 잘린/이상한 파일
  return null;
}
const fmtVer = v => v.map((n, i) => i < 3 ? String(n).padStart(2, "0") : n).join("."); // yy.MM.dd.n
const parseVer = s => (s || "0.0.0.0").split(".").map(Number);
const cmpVer = (a, b) => { for (let i = 0; i < 4; i++) if ((a[i] || 0) !== (b[i] || 0)) return (a[i] || 0) - (b[i] || 0); return 0; };

// 관리자 → 에이전트 DataChannel ("update" 요청, 규칙상 레벨 9만). result = 에이전트의 {ok, msg} 응답.
async function adminChannel(pcId) {
  let gotCh; const chP = new Promise(r => gotCh = r);
  const conn = await openSession(pcId, "update", null, c => { c.ondatachannel = e => gotCh(e.channel); });
  try {
    const ch = await Promise.race([chP, new Promise((_, rej) => setTimeout(() => rej(new Error("연결 시간 초과")), 30000))]);
    ch.binaryType = "arraybuffer";
    if (ch.readyState !== "open") await new Promise(r => ch.onopen = r);
    const result = new Promise((res, rej) => {
      ch.onmessage = e => { const m = JSON.parse(e.data); m.ok ? res(m.msg) : rej(new Error(m.msg)); };
      ch.onclose = () => rej(new Error("연결이 끊겼습니다"));
    });
    result.catch(() => {}); // 전송 중 거부되면 호출한 쪽 await에서 처리
    return { conn, ch, result };
  } catch (e) { conn.close(); throw e; }
}

// TURN 키는 DB·exe에 두지 않고 암호화된 DataChannel로 PC에 직접 전달 (빈 값 = 삭제)
async function pushTurn(pcId, keyId, token, show) {
  show("연결 중...");
  const { conn, ch, result } = await adminChannel(pcId);
  try {
    ch.send(JSON.stringify({ t: "turn", keyId, token }));
    show("완료: " + await result);
  } finally { conn.close(); }
}

async function pushUpdate(pcId, buf, ver, show, force = false, sig = null) {
  show("연결 중...");
  const { conn, ch, result } = await adminChannel(pcId);
  try {
    ch.send(JSON.stringify({ t: "start", size: buf.byteLength, version: fmtVer(ver), force, sig })); // force: 버전 비교 없이 설치, sig: exe 서명
    const CHUNK = 16384;
    ch.bufferedAmountLowThreshold = 256 * 1024;
    for (let o = 0; o < buf.byteLength && ch.readyState === "open"; o += CHUNK) {
      if (ch.bufferedAmount > 1024 * 1024) await Promise.race([new Promise(r => ch.onbufferedamountlow = r), result.catch(() => {})]);
      if (ch.readyState !== "open") break;
      ch.send(buf.slice(o, o + CHUNK));
      show(`전송 중 ${Math.floor(o * 100 / buf.byteLength)}%`);
    }
    if (ch.readyState === "open") { ch.send(JSON.stringify({ t: "end" })); show("설치 중..."); }
    show("완료: " + await result);
  } finally { conn.close(); }
}

// 관리자: 계정 만들기. 기본 앱으로 가입하면 관리자 본인이 새 계정으로 바뀌어 로그인되므로, 별도 앱 인스턴스로 만들고 바로 로그아웃
const creator = initializeAuth(initializeApp(firebaseConfig, "creator"), { persistence: inMemoryPersistence });
$("newUser").onclick = async () => {
  const id = $("newId").value.trim(), pw = $("newPw").value, lv = +$("newLevel").value;
  if (!/^[A-Za-z0-9._-]{2,40}$/.test(id)) return status("아이디는 영문·숫자·._- 2~40자로 입력하세요.");
  if (pw.length < 6) return status("임시 비밀번호는 6자 이상이어야 합니다.");
  try {
    const { user } = await createUserWithEmailAndPassword(creator, toEmail(id), pw);
    await signOut(creator);
    await update(ref(db), { [`users/${user.uid}/name`]: id, [`users/${user.uid}/level`]: lv });
    $("newId").value = ""; $("newPw").value = "";
    status(`계정 만듦: ${id} (레벨 ${lv}) — 임시 비밀번호를 전달하세요.`);
  } catch (e) { fail(e); }
};

function startAdmin() {
  let users = {}, pres = {};
  // 로그인 중인 창들 (계정·IP·기기·시각). 강제 로그아웃은 그 계정의 모든 창.
  const renderPres = () => {
    const tb = $("presList"); tb.replaceChildren();
    const rows = Object.entries(pres).flatMap(([uid, ss]) => Object.entries(ss || {}).map(([sid, s]) => ({ uid, sid, ...s })))
      .sort((a, b) => (b.at || 0) - (a.at || 0));
    for (const s of rows) {
      const mine = s.uid === auth.currentUser.uid, here = presRef?.key === s.sid;
      row(tb, [(users[s.uid]?.name || s.uid) + (here ? " (이 창)" : ""), (s.ip || "?") + (s.cc ? ` (${s.cc})` : ""), s.ua || "", s.at ? new Date(s.at).toLocaleString() : "",
        mine ? "" : el("button", { textContent: "강제 로그아웃", className: "danger" }, { click: () => {
          if (!confirm(`${users[s.uid]?.name || s.uid} 계정을 모든 곳에서 로그아웃시킬까요?`)) return;
          set(ref(db, `users/${s.uid}/logoutBefore`), serverTimestamp()).then(() => status("강제 로그아웃했습니다."), fail);
        } })]);
    }
  };
  unsubs.push(onValue(ref(db, "presence"), s => { pres = s.val() || {}; renderPres(); }));
  unsubs.push(onValue(ref(db, "users"), s => {
    users = s.val() || {};
    renderPres();
    const tb = $("userList"); tb.replaceChildren();
    for (const [uid, u] of Object.entries(users)) {
      if (u.deleted) continue;
      const sel = el("select", {}, { change: e => update(ref(db, `users/${uid}`), { level: +e.target.value }).catch(fail) });
      for (const l of [0, 1, 2, 3, 4, 5, 6, 7, 8, 9]) sel.append(el("option", { value: l, textContent: l, selected: u.level === l }));
      // 로그인 계정 자체는 브라우저에서 못 지움(Admin SDK 필요) → deleted 표시 + 레벨 0으로 막음. 완전 삭제는 Firebase 콘솔 Authentication.
      const del = uid === auth.currentUser.uid ? "" : el("button", { textContent: "삭제", className: "danger" }, { click: () =>
        confirm(`${u.name} 계정을 삭제할까요? 더 이상 로그인할 수 없습니다.`) &&
        update(ref(db, `users/${uid}`), { level: 0, deleted: true }).then(() => status(`${u.name} 계정 삭제됨`), fail) });
      // 강제 해제로 차단된 계정: 여기서 다시 허용 (DB 규칙·에이전트가 blocked면 원격 거부)
      const block = el("button", { textContent: u.blocked ? "차단 해제" : "원격 차단", className: u.blocked ? "primary" : "" },
        { click: () => set(ref(db, `users/${uid}/blocked`), u.blocked ? null : true).catch(fail) });
      row(tb, [u.name + (u.blocked ? " 🚫" : ""), uid, sel, uid === auth.currentUser.uid ? "" : block, del]);
    }
  }));
  // PC는 에이전트가 첫 실행 때 자동 등록. agentInfo(IP·사용자·OS)는 에이전트가 시작할 때마다 갱신.
  let pcs = {}, info = {}, ipNames = {};
  const fmt = t => t ? new Date(t).toLocaleString() : "";
  // 입력칸에 입력 중이면 다시 그리지 않고 포커스가 빠질 때 그림 (PC heartbeat마다 표가 갱신돼 입력값이 사라지던 문제)
  let deferred = false;
  $("admin").onfocusout = () => setTimeout(() => deferred && render());
  const render = () => {
    const a = document.activeElement;
    if ($("admin").contains(a) && ["text", "number"].includes(a.type)) { deferred = true; return; }
    deferred = false;
    let tb = $("pcAdminList"); tb.replaceChildren();
    for (const [id, p] of Object.entries(pcs)) {
      const setPc = v => update(ref(db, `pcs/${id}`), v).catch(fail);
      const a = info[p.agentUid] || {};
      row(tb, [id,
        p.name ?? "",
        pcState(p), p.version, a.ip, a.machineName, a.user, a.os, fmt(a.updatedAt),
        el("input", { type: "number", value: p.minLevel ?? 1, style: "width:4em" }, { change: e => setPc({ minLevel: +e.target.value }) }),
        el("input", { type: "checkbox", checked: !!p.allowGuest }, { change: e => setPc({ allowGuest: e.target.checked }) }),
        el("button", { textContent: "이름 변경" }, { click: () => renamePc(id, p.name) }),
        el("button", { textContent: "삭제", className: "danger" }, { click: () => deletePc(id, p) })]);
    }
    tb = $("ipList"); tb.replaceChildren();
    for (const [k, name] of Object.entries(ipNames)) {
      row(tb, [k.replaceAll("_", "."),
        el("input", { value: name, style: "width:10em" }, { change: e => setIpName(k, e.target.value.trim()) }),
        el("button", { textContent: "삭제", className: "danger" }, { click: () => set(ref(db, `ipNames/${k}`), null).catch(fail) })]);
    }
  };
  unsubs.push(onValue(ref(db, "loginFails"), s => {
    const tb = $("failList"); tb.replaceChildren();
    for (const [k, n] of Object.entries(s.val() || {}))
      row(tb, [toId(k.replaceAll(",", ".")), n >= MAX_FAILS ? `잠김 (${n}회)` : `${n}회`,
        el("button", { textContent: "해제" }, { click: () => set(ref(db, `loginFails/${k}`), null).catch(fail) })]);
  }));
  for (const [path, assign] of [["pcs", v => pcs = v], ["agentInfo", v => info = v], ["ipNames", v => ipNames = v]])
    unsubs.push(onValue(ref(db, path), s => { assign(s.val() || {}); render(); }));

  // 접속 승인 대기 시간: 원격 PC 승인 창이 이 시간 안에 응답 없으면 자동 취소 (에이전트가 읽음).
  unsubs.push(onValue(ref(db, "settings/approvalTimeout"), s => $("approvalTimeout").value = s.val() || 30));
  // 해외 IP 차단: 값이 없으면 켜짐(true)으로 취급 — 에이전트와 같음
  unsubs.push(onValue(ref(db, "settings/blockForeign"), s => $("blockForeign").checked = s.val() !== false));
  $("blockForeign").onchange = e => {
    const on = e.target.checked;
    if (!on && !confirm("해외 IP 차단을 끌까요? 해외에서도 로그인·원격 접속이 가능해집니다.")) { e.target.checked = true; return; }
    set(ref(db, "settings/blockForeign"), on).then(() => status(on ? "해외 IP 차단 켬" : "해외 IP 차단 끔"), fail);
  };
  $("approvalTimeout").onchange = e => {
    const v = Math.round(+e.target.value);
    if (!(v >= 10 && v <= 600)) return status("승인 대기 시간은 10~600초로 입력하세요.");
    set(ref(db, "settings/approvalTimeout"), v).then(() => status(`승인 대기 시간: ${v}초`), fail);
  };
  // IP별 이름: 새로 등록되는 PC의 기본 이름. 지정하면 그 IP의 기존 PC 이름도 바꿈.
  const setIpName = (k, name) => {
    if (!name) return status("이름을 입력하세요.");
    const upd = { [`ipNames/${k}`]: name };
    for (const [id, p] of Object.entries(pcs)) if (ipKey(info[p.agentUid]?.ip) === k) upd[`pcs/${id}/name`] = name;
    update(ref(db), upd).catch(fail);
  };
  let updBuf = null, updVer = null, updSig = null;
  // exe + 서명 파일(.sig, build-agent.ps1이 만듦)을 함께 선택. 서명 확인 기능이 있는 에이전트는 서명 없으면 설치 거부.
  $("updFile").onchange = async e => {
    const files = [...e.target.files], f = files.find(x => /\.exe$/i.test(x.name)), s = files.find(x => /\.sig$/i.test(x.name));
    updBuf = f ? await f.arrayBuffer() : null;
    updVer = updBuf && exeVersion(updBuf);
    updSig = s ? (await s.text()).trim() : null;
    $("updInfo").textContent = !f ? (s ? "exe도 같이 선택하세요" : "") : `${updVer ? "버전 " + fmtVer(updVer) : "버전 정보 없음"} (${(f.size / 1048576).toFixed(1)}MB) · ` +
      (updSig ? "서명 ✓" : "서명 없음 — .sig 파일도 같이 선택하세요 (새 에이전트는 서명 없으면 거부)");
    $("updGo").disabled = !updVer;                                  // 자동 업데이트는 버전 비교가 필요
    $("dlUp").disabled = !f || !/\.exe$/i.test(f.name);              // 다운로드 페이지는 exe면 올릴 수 있음
  };
  // 다운로드 페이지(download.html)용: exe를 3MB씩 base64로 DB에 저장 (RTDB 문자열 하나는 10MB 제한)
  $("dlUp").onclick = async () => {
    const show = s => $("dlInfo").textContent = s, CHUNK = 3 * 1024 * 1024, n = Math.ceil(updBuf.byteLength / CHUNK);
    $("dlUp").disabled = true;
    try {
      await set(ref(db, "downloads/agent"), null); // 받는 중인 사람이 섞인 파일을 받지 않도록 meta부터 지움
      for (let i = 0; i < n; i++) {
        show(`올리는 중 ${i + 1}/${n}`);
        const url = await new Promise((res, rej) => { const r = new FileReader(); r.onload = () => res(r.result); r.onerror = rej; r.readAsDataURL(new Blob([updBuf.slice(i * CHUNK, (i + 1) * CHUNK)])); });
        await set(ref(db, `downloads/agent/chunks/${i}`), url.slice(url.indexOf(",") + 1));
      }
      const version = updVer ? fmtVer(updVer) : "알 수 없음";
      await set(ref(db, "downloads/agent/meta"), { version, size: updBuf.byteLength, chunks: n, uploadedAt: serverTimestamp() });
      show(`완료: ${version} 올림`);
    } catch (e) { show("실패: " + (e.message || e)); }
    $("dlUp").disabled = false;
  };
  // 지금 온라인인 PC 중 버전이 더 낮은 PC에만 전송 (에이전트도 받은 파일의 버전을 다시 확인)
  $("updGo").onclick = () => {
    const tb = $("updList"); tb.replaceChildren();
    const online = Object.entries(pcs).filter(([, p]) => isOnline(p));
    if (!online.length) return status("온라인 PC가 없습니다.");
    // 강제 설치 버전: 비우면 파일보다 낮은 PC만, 적으면 그 버전 이하 PC는 같거나 높아도 무조건 설치
    const upToText = $("updUpTo").value.trim();
    if (upToText && !/^\d+(\.\d+){0,3}$/.test(upToText)) return status("업데이트할 버전은 26.11.13.17 같은 형식으로 입력하세요.");
    const upTo = upToText ? parseVer(upToText) : null;
    const forced = online.filter(([, p]) => upTo && cmpVer(parseVer(p.version), upTo) <= 0 && cmpVer(parseVer(p.version), updVer) >= 0);
    if (forced.length && !confirm(`${forced.length}대는 이미 ${fmtVer(updVer)} 이상인데 강제로 설치합니다` +
      (forced.some(([, p]) => cmpVer(parseVer(p.version), updVer) > 0) ? " (일부는 낮은 버전으로 되돌림)" : "") + ". 계속할까요?")) return;
    for (const [id, p] of online) {
      const st = el("span");
      row(tb, [`${p.name || id} (${id})`, p.version || "?", st]);
      const cur = parseVer(p.version), force = !!upTo && cmpVer(cur, upTo) <= 0;
      if (!force && cmpVer(cur, updVer) >= 0) {
        st.textContent = cmpVer(cur, updVer) === 0 ? "같은 버전 (건너뜀)" : `파일(${fmtVer(updVer)})보다 높음 (건너뜀) — 낮추려면 "업데이트할 버전"에 ${p.version} 이상 입력`;
        continue;
      }
      pushUpdate(id, updBuf, updVer, s => st.textContent = (force ? "[강제] " : "") + s, force, updSig).catch(e => st.textContent = "실패: " + (e.message || e));
    }
  };
  $("turnGo").onclick = () => {
    const keyId = $("turnId").value.trim(), token = $("turnToken").value.trim();
    if (!keyId !== !token) return status("Turn Token ID와 API Token을 둘 다 입력하세요 (둘 다 비우면 삭제).");
    if (!keyId && !confirm("온라인 PC 전체에서 TURN 설정을 삭제할까요?")) return;
    const online = Object.entries(pcs).filter(([, p]) => isOnline(p));
    if (!online.length) return status("온라인 PC가 없습니다.");
    const tb = $("turnList"); tb.replaceChildren();
    for (const [id, p] of online) {
      const st = el("span");
      row(tb, [`${p.name || id} (${id})`, st]);
      pushTurn(id, keyId, token, s => st.textContent = s).catch(e => st.textContent = "실패: " + (e.message || e));
    }
    $("turnToken").value = "";
  };
  $("addIp").onclick = () => {
    const ip = $("nIp").value.trim();
    if (!/^\d{1,3}(\.\d{1,3}){3}$/.test(ip)) return status("IPv4 주소를 입력하세요.");
    setIpName(ipKey(ip), $("nIpName").value.trim());
  };
  unsubs.push(onValue(query(ref(db, "logs"), orderByChild("startedAt"), limitToLast(100)), s => {
    const tb = $("logList"); tb.replaceChildren();
    const logs = Object.values(s.val() || {}).sort((a, b) => b.startedAt - a.startedAt);
    for (const l of logs) row(tb, [fmt(l.startedAt), fmt(l.endedAt), l.pcId, l.mode, users[l.uid]?.name || l.uid]);
  }));
}
