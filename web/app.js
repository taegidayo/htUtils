import { initializeApp } from "https://www.gstatic.com/firebasejs/11.0.2/firebase-app.js";
import { initializeAuth, inMemoryPersistence, onAuthStateChanged, signInAnonymously, signInWithEmailAndPassword, createUserWithEmailAndPassword, signOut }
  from "https://www.gstatic.com/firebasejs/11.0.2/firebase-auth.js";
import { getDatabase, ref, get, set, update, push, onValue, onChildAdded, off, query, orderByChild, limitToLast, serverTimestamp, runTransaction }
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
const LOGIN_DOMAIN = "thinkremote.local";
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
$("signUp").onclick = () => createUserWithEmailAndPassword(auth, toEmail($("email").value), $("pw").value).catch(fail);
$("signOut").onclick = () => signOut(auth);
// Enter 키: 아이디/비밀번호 칸 → 로그인, 게스트 코드 칸 → 접속 (한글 입력 조합 중의 Enter는 무시)
const onEnter = (ids, btn) => ids.forEach(id => $(id).addEventListener("keydown", e => e.key === "Enter" && !e.isComposing && $(btn).click()));
onEnter(["email", "pw"], "signIn");
onEnter(["gPc", "gCode"], "gGo");

let unsubs = [];
onAuthStateChanged(auth, async user => {
  unsubs.forEach(u => u()); unsubs = [];
  const account = user && !user.isAnonymous;
  $("login").hidden = account; $("pcs").hidden = !account; $("admin").hidden = true;
  if (!account) return;
  const name = toId(user.email);
  $("me").textContent = name;
  const me = ref(db, `users/${user.uid}`);
  const failRef = ref(db, `loginFails/${failKey(user.email)}`);
  const fails = (await get(failRef).catch(() => null))?.val() || 0;
  const prof = await get(me).catch(() => null); // 잠긴 계정은 규칙이 읽기를 막음
  if (fails >= MAX_FAILS && prof?.val()?.level !== 9) { status(LOCKED); return signOut(auth); }
  if (fails) set(failRef, null).catch(() => {}); // 로그인 성공 → 연속 실패 횟수 초기화
  // 최초 로그인: 레벨 0으로 프로필 생성 (자식 경로별 규칙이 적용되도록 multi-path update)
  if (prof && !prof.exists()) await update(ref(db), { [`users/${user.uid}/name`]: name, [`users/${user.uid}/level`]: 0 }).catch(fail);
  let pcs = {}, admin = false;
  const render = () => {
    const tb = $("pcList"); tb.replaceChildren();
    for (const [id, p] of Object.entries(pcs)) {
      const on = isOnline(p), actions = el("span");
      if (on) actions.append(el("button", { textContent: "원격 접속" }, { click: () => connect(id, "account").catch(fail) }));
      if (admin) actions.append( // 관리자: 이름 변경 / 삭제 (DB 규칙도 레벨 9만 허용)
        el("button", { textContent: "이름 변경" }, { click: () => renamePc(id, p.name) }),
        el("button", { textContent: "삭제" }, { click: () => deletePc(id, p) }));
      row(tb, [`${p.name || id} (${id})`, el("span", { textContent: on ? "온라인" : "오프라인", className: on ? "on" : "off" }),
        String(p.minLevel ?? ""), actions]);
    }
  };
  unsubs.push(onValue(me, s => {
    const lv = s.val()?.level ?? 0;
    $("me").textContent = `${name} (레벨 ${lv})`;
    admin = lv === 9;
    if (admin && $("admin").hidden) startAdmin();
    $("admin").hidden = !admin;
    render();
  }));
  unsubs.push(onValue(ref(db, "pcs"), s => { pcs = s.val() || {}; render(); }));
  const t = setInterval(render, 15000); unsubs.push(() => clearInterval(t));
});

// ---------- 원격 접속 ----------
const video = $("video");
let pc = null, dc = null;

// 요청 작성 → 에이전트 승인 대기 → 에이전트의 offer에 answer. setup(conn)에서 이벤트 연결. 실패 시 throw.
async function openSession(pcId, mode, code, setup) {
  const reqRef = push(ref(db, `requests/${pcId}`));
  const req = { uid: auth.currentUser.uid, mode, status: "pending", createdAt: serverTimestamp() };
  if (code) req.code = code;
  await set(reqRef, req); // DB 규칙이 레벨/코드/게스트 허용/관리자 여부를 검사 → 실패 시 PERMISSION_DENIED

  const result = await new Promise(res => {
    const r = ref(db, `requests/${pcId}/${reqRef.key}/status`);
    const timer = setTimeout(() => { off(r); res("timeout"); }, 60000);
    onValue(r, s => { if (s.val() && s.val() !== "pending") { clearTimeout(timer); off(r); res(s.val()); } }, () => res("denied"));
  });
  if (result !== "approved") throw new Error(result === "timeout" ? "응답이 없습니다 (PC가 오프라인일 수 있음)." : "접속이 거부되었습니다.");

  const sRef = `sessions/${pcId}/${reqRef.key}`;
  const s = (await get(ref(db, sRef))).val();
  const conn = new RTCPeerConnection({ iceServers: s.iceServers });
  setup(conn);
  await conn.setRemoteDescription(s.offer);
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
async function connect(pcId, mode, code) {
  status(mode === "guest" ? "원격 PC의 승인을 기다리는 중..." : "연결 대기 중...");
  let connected;
  const up = new Promise((res, rej) => connected = { res, rej });
  up.catch(() => {}); // 시간 초과 후 늦게 실패해도 무시
  pc = await openSession(pcId, mode, code, c => {
    c.ontrack = e => { video.srcObject = e.streams[0] || new MediaStream([e.track]); video.play().catch(() => {}); };
    c.ondatachannel = e => {
      console.log("[remote] 채널", e.channel.label);
      if (e.channel.label === "file") { fileCh = e.channel; return; }
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
  if (m.t === "cur") video.style.cursor = m.c;
  if (m.t === "mons") $("mons").replaceChildren(...(m.list.length < 2 ? [] : m.list.map(s =>
    el("button", { textContent: `모니터 ${s.i + 1}${s.primary ? " (주)" : ""} ${s.w}×${s.h}`, className: s.i === m.cur ? "cur" : "" },
      { click: () => { send({ t: "mon", i: s.i }); video.focus(); } }))));
}

// ---------- 파일 보내기: 원격 화면에 드래그앤드롭 → "file" 채널로 한 개씩 전송 → 원격 PC 바탕화면에 저장 ----------
let fileCh = null, fileQueue = Promise.resolve();
const xfer = s => $("xfer").textContent = s;
function sendFile(f) {
  return new Promise((resolve, reject) => {
    const ch = fileCh;
    if (ch?.readyState !== "open") return reject(new Error("파일 채널이 열려 있지 않음"));
    ch.binaryType = "arraybuffer";
    ch.onmessage = e => { const m = JSON.parse(e.data); m.t === "saved" ? resolve(m.name) : reject(new Error(m.msg)); };
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
const viewerEl = $("viewer");
viewerEl.addEventListener("dragover", e => { e.preventDefault(); $("drop").style.display = "flex"; });
viewerEl.addEventListener("dragleave", e => { if (e.target === viewerEl || !viewerEl.contains(e.relatedTarget)) $("drop").style.display = "none"; });
viewerEl.addEventListener("drop", e => {
  e.preventDefault(); $("drop").style.display = "none";
  for (const f of e.dataTransfer.files) // 폴더는 지원 안 함 (파일만)
    fileQueue = fileQueue.then(() => sendFile(f)).then(name => xfer(`완료: ${name} → 원격 PC 바탕화면`), err => xfer(`실패: ${f.name} — ${err.message}`));
  video.focus();
});

function hangUp() {
  clearInterval(heartbeat);
  if (!pc) return;
  pc.close(); pc = null; dc = null; fileCh = null;
  video.srcObject = null; video.style.cursor = ""; $("mons").replaceChildren();
  $("viewer").style.display = "none";
  status("연결 종료");
}
$("hangUp").onclick = hangUp;
// 탭 닫기·새로고침·이동 시 즉시 연결 종료 (에이전트가 바로 감지)
addEventListener("pagehide", hangUp);

// ---------- 입력 전송 (DataChannel) ----------
const send = m => dc?.readyState === "open" && dc.send(JSON.stringify(m));
function pos(e) {
  const r = video.getBoundingClientRect(), vw = video.videoWidth, vh = video.videoHeight;
  if (!vw) return null;
  const sc = Math.min(r.width / vw, r.height / vh);
  const clamp = v => Math.max(0, Math.min(1, v));
  return { x: clamp((e.clientX - r.left - (r.width - vw * sc) / 2) / (vw * sc)),
           y: clamp((e.clientY - r.top - (r.height - vh * sc) / 2) / (vh * sc)) };
}
video.addEventListener("mousemove", e => { const p = pos(e); p && send({ t: "mm", ...p }); });
video.addEventListener("mousedown", e => { video.focus(); send({ t: "md", b: e.button }); e.preventDefault(); });
video.addEventListener("mouseup", e => send({ t: "mu", b: e.button }));
video.addEventListener("contextmenu", e => e.preventDefault());
video.addEventListener("wheel", e => { send({ t: "wh", d: Math.sign(e.deltaY) * 120 }); e.preventDefault(); }, { passive: false });
video.addEventListener("keydown", e => { send({ t: "kd", k: e.keyCode }); e.preventDefault(); });
video.addEventListener("keyup", e => { send({ t: "ku", k: e.keyCode }); e.preventDefault(); });

// ---------- 터치 (모바일): 탭=클릭, 길게 누름=우클릭, 끌기=드래그 (스크롤 모드면 휠), 두 손가락=브라우저 확대/이동 ----------
// touch의 pointerdown에서 preventDefault → 브라우저가 흉내 내는 mousedown/up이 안 생겨 위 마우스 처리와 겹치지 않음
const T = { id: null, x0: 0, y0: 0, wy: 0, moved: false, long: false, timer: 0, fingers: 0 };
let scrollMode = false;
const moveTo = (x, y) => { const p = pos({ clientX: x, clientY: y }); p && send({ t: "mm", ...p }); };
const clickBtn = b => { send({ t: "md", b }); send({ t: "mu", b }); };
function cancelTouch() {
  clearTimeout(T.timer);
  if (T.id !== null && T.moved && !scrollMode && !T.long) send({ t: "mu", b: 0 }); // 드래그 중이었으면 놓기
  T.id = null;
}
video.addEventListener("pointerdown", e => {
  if (e.pointerType !== "touch") return;
  e.preventDefault();
  if (++T.fingers > 1) return cancelTouch(); // 두 번째 손가락 = 확대/이동 제스처
  Object.assign(T, { id: e.pointerId, x0: e.clientX, y0: e.clientY, wy: e.clientY, moved: false, long: false });
  T.timer = setTimeout(() => { T.long = true; moveTo(T.x0, T.y0); clickBtn(2); navigator.vibrate?.(30); }, 550);
});
video.addEventListener("pointermove", e => {
  if (e.pointerType !== "touch" || e.pointerId !== T.id || T.long) return;
  if (!T.moved) {
    if (Math.hypot(e.clientX - T.x0, e.clientY - T.y0) < 10) return; // 손떨림은 탭으로 취급
    T.moved = true; clearTimeout(T.timer);
    if (!scrollMode) { moveTo(T.x0, T.y0); send({ t: "md", b: 0 }); }
  }
  if (!scrollMode) return moveTo(e.clientX, e.clientY);
  const d = T.wy - e.clientY; // 손가락 위로 = 아래로 스크롤
  if (Math.abs(d) >= 30) { send({ t: "wh", d: Math.sign(d) * 120 }); T.wy = e.clientY; }
});
video.addEventListener("pointerup", e => {
  if (e.pointerType !== "touch") return;
  T.fingers = Math.max(0, T.fingers - 1);
  if (e.pointerId !== T.id) return;
  clearTimeout(T.timer); T.id = null;
  if (T.long) return;                                   // 이미 우클릭함
  if (T.moved) { if (!scrollMode) send({ t: "mu", b: 0 }); return; }
  moveTo(e.clientX, e.clientY); clickBtn(0);            // 탭 (빠르게 두 번 = 더블클릭)
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

async function pushUpdate(pcId, buf, ver, show) {
  show("연결 중...");
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
    result.catch(() => {}); // 전송 중 거부되면 아래 await에서 처리
    ch.send(JSON.stringify({ t: "start", size: buf.byteLength, version: fmtVer(ver) }));
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

function startAdmin() {
  let users = {};
  unsubs.push(onValue(ref(db, "users"), s => {
    users = s.val() || {};
    const tb = $("userList"); tb.replaceChildren();
    for (const [uid, u] of Object.entries(users)) {
      const sel = el("select", {}, { change: e => update(ref(db, `users/${uid}`), { level: +e.target.value }).catch(fail) });
      for (const l of [0, 1, 2, 9]) sel.append(el("option", { value: l, textContent: l, selected: u.level === l }));
      row(tb, [u.name, uid, sel]);
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
        el("span", { textContent: isOnline(p) ? "온라인" : "오프라인", className: isOnline(p) ? "on" : "off" }), p.version, a.ip, a.machineName, a.user, a.os, fmt(a.updatedAt),
        el("input", { type: "number", value: p.minLevel ?? 1, style: "width:4em" }, { change: e => setPc({ minLevel: +e.target.value }) }),
        el("input", { type: "checkbox", checked: !!p.allowGuest }, { change: e => setPc({ allowGuest: e.target.checked }) }),
        el("button", { textContent: "이름 변경" }, { click: () => renamePc(id, p.name) }),
        el("button", { textContent: "삭제" }, { click: () => deletePc(id, p) })]);
    }
    tb = $("ipList"); tb.replaceChildren();
    for (const [k, name] of Object.entries(ipNames)) {
      row(tb, [k.replaceAll("_", "."),
        el("input", { value: name, style: "width:10em" }, { change: e => setIpName(k, e.target.value.trim()) }),
        el("button", { textContent: "삭제" }, { click: () => set(ref(db, `ipNames/${k}`), null).catch(fail) })]);
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

  // IP별 이름: 새로 등록되는 PC의 기본 이름. 지정하면 그 IP의 기존 PC 이름도 바꿈.
  const setIpName = (k, name) => {
    if (!name) return status("이름을 입력하세요.");
    const upd = { [`ipNames/${k}`]: name };
    for (const [id, p] of Object.entries(pcs)) if (ipKey(info[p.agentUid]?.ip) === k) upd[`pcs/${id}/name`] = name;
    update(ref(db), upd).catch(fail);
  };
  let updBuf = null, updVer = null;
  $("updFile").onchange = async e => {
    const f = e.target.files[0];
    updBuf = f ? await f.arrayBuffer() : null;
    updVer = updBuf && exeVersion(updBuf);
    $("updInfo").textContent = !f ? "" : `${updVer ? "버전 " + fmtVer(updVer) : "버전 정보 없음"} (${(f.size / 1048576).toFixed(1)}MB)`;
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
    for (const [id, p] of online) {
      const st = el("span");
      row(tb, [`${p.name || id} (${id})`, p.version || "?", st]);
      if (cmpVer(parseVer(p.version), updVer) >= 0) { st.textContent = "최신 버전 (건너뜀)"; continue; }
      pushUpdate(id, updBuf, updVer, s => st.textContent = s).catch(e => st.textContent = "실패: " + (e.message || e));
    }
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
