# Conteggio preciso degli agenti dei workflow e dei subagenti — Design

Data: 2026-09-27
Estende: [2026-09-24-subagenti-sessioni-app-cloud-design.md](2026-09-24-subagenti-sessioni-app-cloud-design.md) §3.1
Si implementa insieme a [2026-09-27-notifiche-app-design.md](2026-09-27-notifiche-app-design.md) (task 10 e 11 dello
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
- Mai durante il replay silenzioso dell'avvio; ogni agente viene chiuso una volta sola.

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
