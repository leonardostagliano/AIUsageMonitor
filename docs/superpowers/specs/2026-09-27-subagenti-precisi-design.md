# Conteggio preciso degli agenti dei workflow e dei subagenti — Design

Data: 2026-09-27
Estende: [2026-09-24-subagenti-sessioni-app-cloud-design.md](2026-09-24-subagenti-sessioni-app-cloud-design.md) §3.1
Si implementa insieme a [2026-09-27-notifiche-app-design.md](2026-09-27-notifiche-app-design.md) (task 10–13 dello
stesso piano).

## 1. Misura

Replay di `~/.aiusagemonitor/events.jsonl` (13–27 settembre 2026) nel `SessionTracker` reale, con lo sweep dei 30 minuti
simulato ogni minuto usando l'ultima attivita' di ogni transcript, confrontando minuto per minuto gli agenti che il
tracker conta con quelli vivi secondo i loro transcript (`<progetto>/<sessione>/subagents/**/agent-<id>.jsonl`, dal
primo all'ultimo timestamp). 11 sessioni su 20 sono sempre giuste; gli errori delle altre hanno tre cause.

| # | Causa | Evidenza | Minuti-agente sbagliati |
|---|---|---|---|
| 1 | `ReconcileSubagents` (sullo `Stop` con `background_tasks`) segna finito ogni agente in esecuzione che l'elenco non nomina. Claude Code elenca il workflow (`type: "workflow"`) ma **mai i suoi agenti**. | Sessione 7ff4d022, 00:59:34: 4 agenti `workflow-subagent` al lavoro → 0; il notch mostra "al lavoro" senza righe fino al `SubagentStart` successivo. | 580 |
| 2 | Un agente che muore senza `SubagentStop` resta in conta. Il suo transcript finisce con una riga utente `[Request interrupted by user]` / `[Request interrupted by user for tool use]` oppure con un messaggio dell'assistente `isApiErrorMessage: true` (es. "You've hit your session limit"). | 278d52c6: 8 agenti fermati dal limite di sessione; 246a13aa e 4acc23d7: agenti interrotti. | 820 |
| 3 | Lo sweep dei 30 minuti visita solo le sessioni senza eventi di subagenti da 30 minuti: finche' un workflow avvia agenti nuovi, i fantasmi della causa 2 non vengono mai liberati. | 246a13aa: "2 agenti" per ore con 1 solo vivo. | amplifica la 2 |

Fuori ambito (minori o storici): un agente ripreso con SendMessage dopo il suo `SubagentStop` non emette un nuovo
`SubagentStart` (80 minuti-agente in due settimane); le sessioni del 13 settembre precedono l'installazione degli hook.

## 2. Soluzione

### 2.1 Riconciliazione (`SessionTracker`, task 10)

- Un agente e' **di workflow** se `AgentType == "workflow-subagent"` oppure il suo `TranscriptPath` sta sotto
  `subagents/workflows/` (separatori `\` o `/`).
- Sullo `Stop` con elenco: un agente di workflow in esecuzione non nominato **resta in esecuzione** se l'elenco contiene
  almeno un workflow; se l'elenco non contiene workflow, e' finito. Gli altri agenti seguono la regola di oggi.
- Su `SubagentStop` con elenco senza alcun workflow: ogni agente di workflow ancora in esecuzione e' finito (nessun
  workflow in volo puo' averne di vivi). Con almeno un workflow nell'elenco non cambia nulla.
- **Sweep per agente.** Ogni sessione con agenti in esecuzione viene visitata a ogni sweep. Un agente con un'attivita'
  nota (ultima riga del suo transcript) e' finito quando quell'attivita' e' piu' vecchia del timeout, qualunque cosa
  facciano gli altri agenti; un agente senza attivita' nota segue la regola di oggi (nessun evento di subagenti nella
  sessione per tutto il timeout). Mentre la sessione e' `NeedsInput` nessun agente viene liberato per inattivita' (puo'
  essere proprio lui ad aspettare un permesso). Gli agenti sintetici di Codex restano esclusi come oggi.
- Il resto dello sweep non cambia: rilascio a `Idle` solo da `Working` in attesa, `PendingWorkflows` azzerato solo quando
  non resta nessun agente.

### 2.2 Fine anomala dal transcript (`HookEventPump`, task 11)

- `SubagentTranscriptEnd.IsTerminated(path)` (Core, puro sul file) legge la coda del transcript (al massimo 64 KB) e
  guarda l'ultima riga `user` o `assistant`: e' terminato se e' una riga `user` il cui testo (stringa o blocco `text`)
  inizia con `[Request interrupted by user`, oppure una riga `assistant` con `isApiErrorMessage: true`. File assente o
  illeggibile → non terminato.
- Nel giro periodico (lo stesso dei token, ogni `TokenRefreshEvery`), per ogni agente Claude in esecuzione il pump trova
  il transcript (`SubagentState.TranscriptPath` o `ClaudeAgentTranscriptLocator`) e, se e' terminato, applica come evento
  live un `SubagentStop` sintetico (`Source = "transcript"`, stesso `AgentId`/`AgentType`), che segue le regole normali
  (ultimo agente di una sessione in attesa → `Idle`).
- Mai durante il replay silenzioso dell'avvio; ogni agente viene chiuso una volta sola. Un agente ripristinato dal
  replay il cui transcript (e `meta.json`) e' stato scritto l'ultima volta prima dell'avvio e' finito ad app chiusa: il
  suo `SubagentStop` e' `Quiet`. Se finisce dopo l'avvio si chiude come un agente live.

## 3. Test

- Tracker: `Stop` con `[workflow]` mantiene 4 agenti di workflow in esecuzione; `Stop` con `[]` li chiude; `SubagentStop`
  con elenco senza workflow chiude gli agenti di workflow rimasti; un agente `general-purpose` non nominato viene chiuso
  come oggi; riconoscimento dal percorso del transcript; sweep per agente (uno fermo da 40 minuti mentre un altro emette
  eventi → solo il primo e' chiuso), nessun rilascio in `NeedsInput`, agente senza attivita' → regola di sessione.
- `SubagentTranscriptEndTests`: interruzione (stringa e blocco `text`, con e senza "for tool use"), errore API, fine
  normale (`tool_result`, testo finale), righe `attachment`/`system` dopo l'ultima riga utile, file assente, righe corrotte.
- `HookEventPumpTests`: agente con transcript terminato → chiuso al giro periodico, una volta sola, mai nel replay.
- Verifica sul campo: lo stesso replay di §1 sul codice nuovo deve azzerare le cause 1 e 3 e ridurre la 2 al ritardo
  del giro periodico.

## 4. Nomi degli agenti e figli di Codex (task 12 e 13)

### 4.1 Misura (Codex, 26–27 settembre)

Confronto tra i rollout dei figli (`~/.codex/sessions/**/rollout-*-<thread id>.jsonl`, eventi `task_started` /
`task_complete` / `turn_aborted`) e gli hook della sessione padre:

| # | Problema | Evidenza |
|---|---|---|
| C1 | Il notch mostra `AgentType`: "default" dagli hook di Codex, "codex-thread" dal fallback. Il nome vero sta nel `session_meta` del rollout del figlio: `agent_nickname` ("Harvey") e `agent_path` ("/root/login_audit"). Per Claude, `AgentType` vale "workflow-subagent"/"general-purpose" mentre `<transcript>/../agent-<id>.meta.json` ha una `description` leggibile ("write:B (tasks 3,6)"). | Harvey, Popper, Anscombe, Pasteur, Aquinas, Boole, Hypatia |
| C2 | Un figlio di Codex e' un thread che riceve piu' turni: un solo `SubagentStart`, un `SubagentStop` per turno (e non sempre: il turno di Anscombe chiuso alle 11:21:17 non ne ha). Dopo il primo `SubagentStop` il tracker lo tiene finito anche mentre lavora ai turni successivi, e il fallback dei rollout e' spento per 6 h (`CodexHookGrace`) nelle sessioni i cui hook riportano subagenti. | Harvey: 8 turni 18:39–00:38, una sola Start |
| C3 | I thread "guardian" di Codex (`thread_source: "guardian_review"`, `source.subagent.other: "guardian"`) hanno `parent_thread_id` del padre e turni da 3–4 s a raffica: il fallback li conterebbe come subagenti. Solo i figli con `source.subagent.thread_spawn` sono subagenti. | 9 thread guardian in una sessione |

`meta.json` di Claude riporta anche `stoppedByUser: true` per un agente fermato dall'utente.

### 4.2 Soluzione

- **Nome (task 12).** `SubagentState` guadagna `string? Name`. Il pump lo risolve nel giro periodico dei token, una volta
  per agente (poi resta): Claude → `description` di `agent-<id>.meta.json` accanto al transcript dell'agente; Codex →
  `agent_nickname` e l'ultimo segmento di `agent_path` ("Harvey · login_audit", solo uno dei due se l'altro manca). La riga
  dell'agente nel notch mostra `Name`, poi `AgentType`, poi l'id corto; il tooltip mostra anche il tipo. Nomi tagliati
  a 60 caratteri; nessun nome nei log.
- **Stato dei figli di Codex (task 13).** Per ogni figlio noto di una sessione Codex, anche quando gli hook riportano
  subagenti, lo scanner legge la coda del rollout del figlio: ultimo evento di turno `task_started` → in esecuzione,
  `task_complete`/`turn_aborted` → finito. Il pump applica come eventi live un `SubagentStart` sintetico quando un figlio
  finito ha un `task_started` piu' recente della sua fine, e un `SubagentStop` sintetico quando un figlio in esecuzione ha
  chiuso il turno senza che l'hook lo dicesse. Gli hook restano il segnale rapido; il rollout decide lo stato. Il
  fallback per le sessioni senza hook resta com'e'.
- **Guardian esclusi (task 13).** Lo scanner considera figlio solo un rollout con `source.subagent.thread_spawn`
  (o, per i rollout vecchi senza `source`, senza `thread_source` diverso da `subagent`); `guardian_review` e ogni
  `source.subagent.other` sono ignorati.
- **Fermato dall'utente (task 11).** `SubagentTranscriptEnd.IsTerminated` considera terminato anche un agente il cui
  `agent-<id>.meta.json` ha `stoppedByUser: true`.

### 4.3 Test

- Nomi: `meta.json` con e senza `description`, file assente o corrotto; rollout Codex con nickname e path, solo uno dei
  due, nessuno; il nome resta dopo essere stato risolto; la riga del notch usa Name → AgentType → id.
- Codex: figlio con 3 turni (Start, Stop, nuovo `task_started` → di nuovo in esecuzione, `task_complete` senza hook →
  finito); turno chiuso senza `SubagentStop`; guardian ignorati (scanner e riconciliazione); rollout vecchio senza
  `source`; nessun evento sintetico nel replay.
