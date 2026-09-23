# Refresh per agente e costi in euro — Design

Data: 2026-09-23
Stato: approvato in brainstorming, in attesa di revisione della spec scritta
Estende: [2026-09-13-aiusagemonitor-design.md](2026-09-13-aiusagemonitor-design.md) (sezioni 6, 7.6, 8.2, 8.3)

## 1. Obiettivo

Due funzionalità:

1. **Refresh a comando per singolo agente.** Ogni card del notch (Claude Code, Codex) ha un pulsante che aggiorna
   subito la quota di quell'agente e ricalcola token e costi delle sue sessioni, senza aspettare il ciclo periodico.
2. **Costi in euro.** Accanto ai token di ogni sessione e di ogni agente di workflow, e come totale per card, l'app
   mostra il **costo API equivalente**: quanto costerebbero quei token ai prezzi di listino pubblicati da Anthropic e
   OpenAI, convertiti in euro al tasso di riferimento BCE. Con un piano in abbonamento (Max, Pro) non è una spesa
   reale, e l'interfaccia lo dice.

## 2. Non obiettivi

- Totali per periodo (oggi, 7 giorni, mese) e scansione storica di transcript e rollout.
- Spesa reale dell'abbonamento o crediti extra usage (restano quelli dell'endpoint quota).
- Valute diverse dall'euro.
- Prezzi batch (i transcript degli agenti non sono mai batch).
- Voce di refresh per agente nel menu della tray (resta la voce globale "Aggiorna ora").
- Prezzo stimato per modelli assenti dal listino: si mostrano come non prezzati (sezione 5.4), senza alias impliciti.

## 3. Decisioni prese in brainstorming

| Tema | Scelta |
|---|---|
| Dove mostrare i costi | Riga sessione, riga agente di workflow, riepilogo agenti e totale per card. |
| Fonte dei prezzi | Listino pubblico scaricato: **LiteLLM** (`model_prices_and_context_window.json`), con fallback a una copia imbarcata e override locale. |
| Cambio USD → EUR | Tasso di riferimento **BCE** giornaliero, con fallback all'ultimo noto e poi a un tasso fisso nelle impostazioni. |
| Cosa aggiorna il ⟳ | Quota dell'agente più token e costi di tutte le sue sessioni. |

### 3.1 Perché LiteLLM

Verificato il 2026-09-23 sui modelli presenti nei dati locali (`claude-fable-5`, `claude-fable-5-1`, `claude-opus-5`,
`claude-opus-5-5`, `gpt-6-astra`, `gpt-6-luna`, `gpt-6-sol`, `gpt-5.6-terra`, `gpt-5.6-luna`): LiteLLM, models.dev e
OpenRouter riportano gli stessi prezzi, ma solo LiteLLM ha tutto quello che serve al calcolo.

- Le chiavi coincidono con gli id scritti nei transcript e nei rollout (`claude-fable-5-1`, non `claude-fable-5.1`).
- Ogni voce ha `source` che punta alla pagina prezzi del vendor (`platform.claude.com/.../pricing`,
  `developers.openai.com/api/docs/pricing`).
- Ha il prezzo separato della **cache scritta a 1 ora** (`cache_creation_input_token_cost_above_1hr`). Claude Code
  scrive solo cache a 1 ora (`ephemeral_1h_input_tokens`; `ephemeral_5m_input_tokens` è sempre 0 nei dati locali): con
  il solo prezzo a 5 minuti le scritture in cache verrebbero sottostimate del 37%.
- Ha le fasce per contesto lungo (`*_above_272k_tokens` per OpenAI, `*_above_200k_tokens` per alcuni modelli
  Anthropic), i prezzi `*_priority` e `*_flex` di OpenAI e i moltiplicatori `provider_specific_entry` (`fast: 2` per
  Opus 5.5 in fast mode, `us: 1.1` per l'inferenza solo USA).

models.dev non ha la cache a 1 ora né i moltiplicatori; OpenRouter è il listino di un rivenditore e usa nomi diversi.
`codex-auto-review` (il revisore delle approvazioni di Codex) non ha prezzo in nessuna delle tre fonti.

## 4. Refresh per agente

### 4.1 Comportamento

- Pulsante ⟳ nell'intestazione della card, a destra del nome e prima del badge del piano.
- Il click avvia in parallelo:
  1. il refresh della quota di quell'agente (`UsageScheduler`), che per Claude chiama l'endpoint usage e per Codex
     rilegge i rollout;
  2. la rilettura dei token di **tutte** le sessioni di quell'agente sul thread del pump, comprese quelle Idle che
     il passaggio periodico salta, non silenziosa (le righe si aggiornano subito);
  3. se il listino o il tasso hanno più di 24 ore, il loro aggiornamento (mai forzato quando sono freschi).
- Mentre il refresh è in corso l'icona ruota e i click vengono ignorati. Il refresh si considera concluso quando
  1 e 2 sono terminati, o dopo **15 s** di timeout; il 3 non viene atteso.
- Dopo la conclusione il pulsante resta disabilitato per **10 s** (cooldown, icona attenuata), per non martellare
  l'endpoint Anthropic.
- Tooltip: "Aggiorna Claude Code" + "Aggiornato alle HH:mm:ss" dopo il primo refresh manuale; durante il cooldown
  "Di nuovo tra N s".
- Il click è marcato `Handled` come fanno `FlatRowButton` e il nome della sessione: non fissa né sblocca il notch.
- Gli esiti restano quelli di oggi: un errore del fetch diventa "non aggiornato" con il messaggio già esistente nella
  card, nessun toast.
- La voce globale "Aggiorna ora" della tray esegue lo stesso refresh per ogni agente abilitato.

### 4.2 Componenti

- `UsageScheduler.RefreshNowAsync(AgentKind)` → `Task`: sveglia il ciclo dell'agente come `RefreshNow` e completa
  quando termina il **prossimo** `RefreshAsync` di quell'agente (anche se era già in corso un refresh periodico, si
  aspetta quello successivo, così il risultato riflette il click). Agente disabilitato: completa subito.
  `RefreshNow` resta per i chiamanti che non aspettano (debounce Codex).
- `HookEventPump.RefreshTokensNowAsync(AgentKind)` → `Task`: sul thread del pump (stesso `_gate`), rilegge token e
  costi di tutte le sessioni dell'agente, non silenzioso. Non tocca `_lastTokenRefresh`.
- `AppServices.RefreshAgentAsync(AgentKind)`: combina i due con il timeout di 15 s, avvia il punto 3 senza
  attenderlo, non solleva mai (gli errori vanno nel log).
- `AgentCardViewModel`: `RefreshCommand` (CanExecute falso durante refresh e cooldown), `IsRefreshing`,
  `RefreshTooltip`. Cooldown con un `DispatcherTimer`; lo stato è per card e non sopravvive a un `Rebuild`, che è
  accettabile.
- `Icons.xaml`: nuova geometria `RefreshIcon`; rotazione con `RotateTransform` animato da un `DataTrigger` su
  `IsRefreshing`, come il `PulseStoryboard` esistente.

## 5. Costi

### 5.1 Conteggio per modello

Oggi i contatori producono un solo `TokenUsage` per transcript. Vengono estesi per produrre, accanto al totale che
non cambia, un **registro d'uso** (`UsageLedger`) diviso in voci. Ogni voce ha una chiave e dei contatori.

Chiave (`UsageKey`):

| Campo | Valori | Fonte |
|---|---|---|
| `Model` | id del modello così come scritto | Claude: `message.model`; Codex: modello in vigore (5.3) |
| `Tier` | `Standard`, `Fast`, `Priority`, `Flex` | Claude: `usage.speed` (`fast` → Fast); Codex: `service_tier` (`priority`/`fast` → Priority, `flex` → Flex, altro → Standard) |
| `Geo` | stringa o null | Claude: `usage.inference_geo` (ignorato se `not_available`/`global`) |
| `ContextBand` | 0, 200 000, 272 000 | la soglia più alta di `PricingThresholds.Known` **superata** dal prompt della richiesta (prompt > soglia), altrimenti 0 |

Contatori (`LedgerTokens`): `Input` (non in cache), `Output`, `CacheRead`, `CacheWrite5m`, `CacheWrite1h`,
`WebSearches`, `Requests`.

La fascia di contesto si decide al conteggio, con le soglie fisse `PricingThresholds.Known = {200 000, 272 000}` (le
uniche presenti oggi nel listino per Anthropic e OpenAI). I prezzi si applicano solo al momento di mostrarli, quindi un
listino aggiornato vale subito, senza rileggere i file. Una soglia del listino che non è in `Known` viene ignorata e
annotata una volta nel log.

Il registro è un valore immutabile con uguaglianza per valore (voci ordinate per chiave), perché il tracker solleva
`Changed` solo quando qualcosa cambia.

### 5.2 Claude Code

`ClaudeTranscriptTokenCounter` mantiene per ogni transcript, accanto a `Total`, il registro. Per ogni richiesta
deduplicata (stessa regola di oggi: `message.id`, poi `requestId`, con la sola crescita aggiunta):

- chiave dal modello, `speed` e `inference_geo` della riga; le righe con modello `<synthetic>` o senza `usage` non
  contano, come oggi;
- `CacheWrite5m`/`CacheWrite1h` da `usage.cache_creation.ephemeral_5m_input_tokens` /
  `ephemeral_1h_input_tokens`; se l'oggetto manca, tutto `cache_creation_input_tokens` va in `CacheWrite5m`;
- `WebSearches` da `usage.server_tool_use.web_search_requests`, con la stessa regola del massimo per richiesta;
- dimensione del prompt = `input_tokens + cache_read_input_tokens + cache_creation_input_tokens` della richiesta.
  Questi tre valori sono già definitivi dalla prima riga; cresce solo l'output.

Il totale `TokenUsage` resta esattamente quello di oggi: la somma delle voci deve coincidere con `Total` (verificato
dai test).

### 5.3 Codex

`CodexTokenCounter` oggi legge solo l'ultimo totale cumulativo, dalla coda del file. Il registro richiede una lettura
**in avanti e incrementale** del rollout, per offset come il contatore Claude, gestita da un componente separato
(`CodexUsageLedgerReader`) per non toccare la lettura del totale.

- Il modello in vigore è l'ultimo `turn_context.payload.model` o `thread_settings_applied.thread_settings.model` visto
  prima del `token_count`; stessa regola per `service_tier`.
- La crescita letta prima che il rollout nomini un modello resta in sospeso e passa sotto il primo modello nominato:
  un subagente forkato scrive il totale ereditato prima del primo `turn_context` (8 rollout locali di luglio, dal 63%
  al 99,5% del loro consumo). Finché nessun modello compare, resta sotto un modello vuoto, quindi non prezzata.
- Ogni `token_count` con `info.total_token_usage` contribuisce la **differenza** rispetto al totale precedente dello
  stesso rollout. Gli eventi ripetuti con lo stesso totale danno zero. Il primo evento contribuisce il suo totale
  intero, anche quando il thread è stato ripreso e parte con un totale ereditato. Verificato il 2026-09-23 su 60
  rollout locali: in 58 su 59 la somma dei `last_token_usage` coincide con il totale finale; il caso che non torna è
  un thread ripreso, che la differenza tra totali copre.
- Un totale che scende (input o output sotto il precedente) è una **ripartenza** e conta per intero, come il primo
  evento. Codex azzera il totale cumulativo quando risveglia un thread subagente per un nuovo task: su 390 rollout
  locali (2026-09-23) ci sono 18 cali in 14 rollout, tutti subito dopo un `task_started`, e in tutti il nuovo totale
  coincide con il `last_token_usage` dell'evento. Così il registro contiene tutto il consumo del rollout.
- **Divario con il totale mostrato.** `CodexTokenCounter` legge solo l'ultimo totale: per un thread con ripartenze
  mostra solo i token dall'ultima ripartenza, mentre il registro li somma tutti (fino a 35 volte tanto nei rollout
  locali). Senza ripartenze la somma delle voci coincide con il totale mostrato. Punto aperto (sezione 10).
- Suddivisione: `CacheRead = cached_input_tokens`, `Input = input_tokens − cached_input_tokens`,
  `CacheWrite5m = cache_write_input_tokens` (OpenAI non distingue la durata), `Output = output_tokens`. I token di
  ragionamento sono già dentro `output_tokens` e si pagano come output.
- Dimensione del prompt per la fascia = `last_token_usage.input_tokens` dell'evento; se manca, l'input della
  differenza.
- File troncato o sostituito: si riparte da zero, come fa il contatore Claude.

### 5.4 Listino

**Formato interno.** `PriceCatalog` immutabile: id modello → `ModelPrice` con i prezzi in USD per token
(`Input`, `Output`, `CacheRead`, `CacheWrite5m`, `CacheWrite1h`), le fasce (`Threshold` → stessi cinque prezzi), le
varianti `Priority` e `Flex` (base e fasce), i moltiplicatori (`fast`, geo) e `WebSearch` per richiesta. Dal listino
LiteLLM si tengono solo le voci con `litellm_provider` `anthropic` o `openai` (216 il 2026-09-23).

Mappatura dei campi LiteLLM:

| Interno | LiteLLM |
|---|---|
| `Input` / `Output` | `input_cost_per_token` / `output_cost_per_token` |
| `CacheRead` | `cache_read_input_token_cost` (assente: `Input`) |
| `CacheWrite5m` | `cache_creation_input_token_cost` (assente: `Input`) |
| `CacheWrite1h` | `cache_creation_input_token_cost_above_1hr` (assente: `CacheWrite5m`) |
| fascia `T` | `*_above_{T/1000}k_tokens` degli stessi campi |
| `Priority` / `Flex` | suffissi `_priority` / `_flex` degli stessi campi |
| moltiplicatori | `provider_specific_entry` (`fast`, e chiavi geo come `us`) |
| `WebSearch` | `search_context_cost_per_query.search_context_size_medium` |

**Risoluzione del nome.** Nell'ordine: id esatto; id in minuscolo senza il suffisso `[1m]` e senza prefisso
`anthropic/` o `openai/`; lo stesso senza suffisso data (`-20251001`, `-2025-10-01`). Nessun'altra somiglianza. Un
modello non risolto è **non prezzato**.

**Calcolo di una voce** (`CostCalculator`, funzione pura): si sceglie il gruppo di prezzi per `Tier` (Priority e Flex
con i relativi campi, ripiego sullo Standard se mancano; Fast = Standard × moltiplicatore `fast`) e per fascia (la più
alta con soglia ≤ `ContextBand`), si moltiplica per il moltiplicatore geo se presente, e si sommano:
`Input×Input + Output×Output + CacheRead×CacheRead + CacheWrite5m×CacheWrite5m + CacheWrite1h×CacheWrite1h +
WebSearches×WebSearch`. Il risultato in USD si divide per il tasso (5.5).

Esito del calcolo: importo in euro più l'elenco dei modelli non prezzati. Solo voci non prezzate: "costo n/d"; alcune
sì e alcune no: l'importo è un minimo (`≥`).

**Sorgenti, in ordine di priorità** (la prima disponibile vince, l'override si applica sopra):

1. `%LOCALAPPDATA%\AIUsageMonitor\prices-override.json`, opzionale, scritto a mano, stesso schema per-modello di
   LiteLLM: aggiunge modelli o sostituisce i campi indicati di quelli esistenti. Un file malformato viene ignorato e
   annotato nel log.
2. `%LOCALAPPDATA%\AIUsageMonitor\prices-cache.json`: l'ultimo download valido, già filtrato e ridotto ai campi
   usati, con `ETag`, data di download e URL.
3. Copia imbarcata nell'exe (`src/AIUsageMonitor.Core/Pricing/prices-snapshot.json`, `EmbeddedResource`), stesso
   formato della cache, rigenerata con `node scripts/update-price-snapshot.mjs`. Garantisce i costi al primo avvio e
   senza rete.

**Download** (`PriceListService`):

- URL `https://raw.githubusercontent.com/BerriAI/litellm/main/model_prices_and_context_window.json`.
- Primo controllo 20 s dopo l'avvio, poi ogni 6 ore; scarica solo se la cache ha più di 24 ore o manca, con
  `If-None-Match` (304 → rinnova solo la data della cache).
- Timeout 30 s, limite 20 MB, nessuna intestazione oltre allo `User-Agent` dell'app, nessun cookie o credenziale.
- Un download che non produce almeno un modello Anthropic e uno OpenAI viene scartato e la cache precedente resta.
- Solo con "Mostra costi" attivo. Fuori dal thread UI; gli errori vanno nel log e lo stato delle Impostazioni mostra
  l'ultimo esito.

### 5.5 Cambio

`ExchangeRateService`:

- URL `https://www.ecb.europa.eu/stats/eurofxref/eurofxref-daily.xml`; si legge l'attributo `rate` del `Cube` con
  `currency='USD'` e la data dal `Cube` con `time` (il 2026-09-23: 1 € = 1,1411 $).
- Cache `%LOCALAPPDATA%\AIUsageMonitor\exchange-rate.json` (tasso, data BCE, data di download). Stessa cadenza del
  listino (20 s dopo l'avvio, poi ogni 6 ore se la cache ha più di 24 ore), timeout 15 s.
- Fallback: ultimo tasso in cache, poi il tasso fisso delle impostazioni (`UsdPerEur`, default 1,14, intervallo
  0,5–2,0). Il tasso usato e la sua origine compaiono nei tooltip e nelle Impostazioni.
- Solo con "Mostra costi" attivo.

### 5.6 Flusso dei dati

```
contatori (thread del pump) ──► UsageLedger per sessione e per subagente ──► SessionTracker ──► StateChanged
PriceListService / ExchangeRateService ──► PricingSnapshot (catalogo + tasso, riferimento immutabile) ──► PricingChanged
                                                                                       │
AgentCardViewModel / SessionRowViewModel / SubagentRowViewModel ◄── CostCalculator(ledger, snapshot) al Refresh
```

- `ITokenSource` riceve due membri con implementazione di default `null`, come `SubagentModels`:
  `SessionLedger(SessionState)` e `SubagentLedgers(SessionState)`. Non fanno IO: restituiscono il registro calcolato
  dalla lettura appena fatta da `SessionTokens` / `SubagentTokens`.
- `SessionState.Ledger` e `SubagentState.Ledger` (default `null`); `SessionTracker.UpdateTokens` e
  `UpdateTokensSilently` accettano i registri come parametri opzionali.
- `PricingChanged` è collegato a `AppServices.StateChanged`, così un nuovo listino o tasso ridisegna le card.

## 6. Interfaccia

### 6.1 Formato

- `≈ 3,21 €` con cultura it-IT: due decimali sotto 100 €, nessun decimale da 100 € in su (`≈ 1.234 €`); sotto un
  centesimo `< 0,01 €`; zero token: nessun costo.
- Minimo per modelli non prezzati: `≥ 3,21 €`. Nulla di prezzato: `costo n/d`.
- Con "Mostra costi" spento nessun testo di costo compare e nessun tooltip lo cita.

### 6.2 Notch

- **Intestazione card:** ⟳ (sezione 4).
- **Totale card**, subito dopo le finestre di quota e l'extra usage: `Costo API equivalente ≈ 12,40 €`, cioè la
  somma di tutte le sessioni dell'agente presenti nel notch e di **tutti** i loro subagenti, anche conclusi. Tooltip:
  "Stima ai prezzi API di listino: non è la spesa dell'abbonamento", poi il dettaglio per modello, il listino (fonte e
  data) e il tasso (valore, data, origine).
- **Riga sessione:** la riga token diventa `↑ 1,2M · ↓ 45k · ≈ 3,21 €`, dove il costo è quello dei token della riga
  (la sola sessione, senza subagenti, come i token). Il tooltip esistente aggiunge il costo per modello, "Con agenti:
  ≈ X €" quando la sessione ne ha, ed eventuali modelli non prezzati.
- **Riga agente di workflow:** stesso formato sulla sua riga token.
- **Riepilogo agenti:** `2 agenti attivi · 350k tok · ≈ 0,80 €` (solo gli agenti in corso, coerente con i token).

### 6.3 Impostazioni

Nuovo gruppo **COSTI**, tra NOTIFICHE e AGGIORNAMENTI:

- `Mostra costi (prezzi API di listino)`, attivo di default. Spento: nessun download e nessun costo nell'interfaccia.
- `Tasso di riserva: 1 € = [1,14] $`, usato solo quando manca il tasso BCE.
- Riga di stato, in sola lettura: `Listino LiteLLM del 23/09 14:02 · 216 modelli` (o `copia imbarcata` /
  `override attivo`) e `BCE 1,1411 del 23/09` (o `tasso di riserva`), con l'ultimo errore se c'è.
- Pulsante `Apri cartella` sulla cartella dati, per chi vuole scrivere `prices-override.json`.

`AppSettings`: `ShowCosts` (bool, default `true`), `UsdPerEur` (double, default `1.14`, normalizzato in 0,5–2,0).

## 7. Gestione errori

| Caso | Comportamento |
|---|---|
| Listino irraggiungibile, 4xx/5xx, timeout, JSON non valido | Resta la cache (o la copia imbarcata); log; stato nelle Impostazioni. |
| Listino valido ma senza modelli Anthropic o OpenAI | Scartato come sopra. |
| BCE irraggiungibile o XML senza USD | Ultimo tasso in cache, poi il tasso di riserva; log. |
| Modello non nel listino | Voce non prezzata: `≥` o `costo n/d`, modello nominato nel tooltip. |
| `prices-override.json` malformato | Ignorato, log una volta per contenuto. |
| Transcript o rollout illeggibile | Il registro resta quello già noto, come il totale di oggi. |
| Refresh manuale oltre 15 s | Il pulsante torna attivo (con cooldown); il refresh continua in background. |
| Eccezione in un servizio di pricing | Mai propagata al thread UI o ai timer; log. |

## 8. Privacy e rete

Due nuove chiamate di rete, entrambe GET anonime senza dati dell'utente, solo con "Mostra costi" attivo:
`raw.githubusercontent.com` (listino LiteLLM) e `www.ecb.europa.eu` (tasso BCE). Il README (sezione Privacy, tabella
dei file, impostazioni) viene aggiornato con: le due chiamate, `prices-cache.json`, `prices-override.json`,
`exchange-rate.json`, il gruppo COSTI e una nuova sezione "Costi" che spiega il costo API equivalente, le fonti e i
limiti.

## 9. Test

Tutti senza rete (`FakeHttpMessageHandler`, `ManualTimeProvider`, `TempDir` esistenti).

- **Listino:** parsing di una fixture LiteLLM ridotta (voci Anthropic e OpenAI, una di un altro provider da
  scartare, fasce 272k, priority/flex, `provider_specific_entry`, cache 1h assente e presente); mappatura dei
  ripieghi; override che aggiunge e sostituisce; override malformato; cache scritta e riletta; 304; download senza
  modelli validi scartato; ordine di priorità delle sorgenti; copia imbarcata leggibile.
- **Nomi:** esatto, `[1m]`, prefissi, suffissi data, maiuscole; nessuna corrispondenza parziale.
- **Calcolo:** voce standard; cache 1h vs 5m; fascia 272k; Fast con moltiplicatore; geo `us`; Priority e Flex con e
  senza campi dedicati; web search; non prezzato (`≥` e n/d); conversione con il tasso; formattazione (soglie 0,01,
  100, migliaia).
- **Contatore Claude:** fixture di transcript con cambio di modello a metà, richieste in streaming con output che
  cresce, `cache_creation` presente e assente, `speed: fast`, web search; somma delle voci uguale a `Total`;
  incrementalità (due letture) e reset su troncamento.
- **Registro Codex:** fixture di rollout con cambio di modello, `token_count` ripetuti, thread ripreso con totale
  iniziale ereditato, totale che scende, `service_tier` priority; somma delle voci uguale al totale cumulativo;
  incrementalità.
- **BCE:** parsing dell'XML reale (fixture), USD mancante, cache e fallback al tasso di riserva.
- **Refresh:** `RefreshNowAsync` completa dopo il refresh successivo al click (anche con un refresh in corso), subito
  per un agente disabilitato, non lascia task appesi alla chiusura; `RefreshTokensNowAsync` legge anche le sessioni
  Idle e solleva `Changed`.
- **Tracker:** un registro che cambia solleva `Changed`, uno uguale no.
- Gli script Node (`scripts/update-price-snapshot.mjs`) hanno un test in `node --test` sulla funzione di filtro.

## 10. Rischi e punti aperti

- **Prezzi LiteLLM sbagliati o in ritardo** su un modello appena uscito: l'override locale e la fonte citata nel
  tooltip permettono di verificarli e correggerli.
- **`codex-auto-review`** resta non prezzato finché un listino non lo pubblica: le sessioni Codex che lo usano
  mostrano `≥`.
- **Fasce di contesto** nuove (soglie diverse da 200k e 272k) richiedono di aggiornare `PricingThresholds.Known`: il
  log lo segnala.
- **Endpoint quota Claude** sotto refresh ripetuti: il cooldown di 10 s più il ciclo periodico lo limitano; un 429
  viene mostrato come oggi ("non aggiornato").
- **Totale Codex dei subagenti risvegliati** (5.3): `CodexTokenCounter` mostra solo i token dall'ultima ripartenza del
  totale cumulativo, il registro tutto il consumo. Da decidere dove si collegano i registri alle righe: mostrare come
  totale `ledger.ToTokenUsage()` quando il registro esiste, oppure accettare e spiegare il divario.
