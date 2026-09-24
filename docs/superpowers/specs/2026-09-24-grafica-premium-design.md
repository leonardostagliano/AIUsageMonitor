# Grafica Premium — Design

Data: 2026-09-24
Stato: approvato in brainstorming, in attesa di revisione della spec scritta
Estende: [2026-09-13-aiusagemonitor-design.md](2026-09-13-aiusagemonitor-design.md) (sezioni 8.1, 8.2, 8.3)
Mockup: canvas "AIUsageMonitor · direzioni visive" (claude.ai, privato), direzione **C · Premium**.

## 1. Obiettivo

Rifare l'aspetto di tutte le superfici dell'app con un solo linguaggio visivo "Premium": più d'impatto, curato e
coerente, senza cambiare cosa l'app fa. Superfici: il **notch** (linguetta e pannello), la finestra **Impostazioni**,
il **menu della tray** e la finestra **Aggiornamento disponibile**.

## 2. Non obiettivi

- Tema chiaro, colori personalizzabili dall'utente.
- Nuove funzioni o nuovi dati (niente storico, grafici o sparkline).
- Cambiare la larghezza del pannello del notch (resta 320 px) o il suo comportamento (hover, pin, ritardo di chiusura).
- Barra del titolo personalizzata per le Impostazioni: resta quella scura di sistema (Snap, trascinamento e pulsanti
  nativi).
- Effetti di sistema (Mica, acrilico, sfocatura dello sfondo).
- Librerie di terze parti: tutto in XAML e C# dell'app (vincolo della spec 3).

## 3. Decisioni prese in brainstorming

| Tema | Scelta |
|---|---|
| Direzione visiva | C · Premium: fondo grafite pieno, numeri grandi, barre spesse con riflesso, alone del colore dell'agente, sessioni come riquadri con avatar. |
| Perimetro | Notch, Impostazioni, menu della tray, finestra di aggiornamento. |
| Movimento | Ricco ma misurato (sezione 5). Tutto spento se Windows ha le animazioni disattivate. |
| Impostazioni | Schede in alto, una sezione per scheda; Salva e Annulla valgono per tutte. |
| Implementazione | Design system interno in XAML più tre componenti riusabili; nessuna dipendenza. |

## 4. Sistema visivo

### 4.1 Colori

Chiavi in `Assets/Theme.xaml`. Le chiavi esistenti restano (cambiano i valori), quelle nuove si aggiungono.

| Chiave | Valore | Uso |
|---|---|---|
| `NotchBackground` | `#F5121216` | pannello e linguetta del notch |
| `WindowBackground` | `#FF121216` | Impostazioni, aggiornamento, menu |
| `Card` | `#FF1B1B21` | card di agente e di impostazione |
| `Tile` | `#0AFFFFFF` | riquadri sessione, righe interne alle card |
| `Surface` / `SurfaceHover` / `SurfacePressed` | `#FF26262E` / `#FF30303A` / `#FF3A3A45` | pulsanti secondari, campi, voci di menu |
| `NotchBorder` / `Hairline` | `#12FFFFFF` / `#0FFFFFFF` | bordi del pannello e delle card |
| `TextPrimary` | `#FFF5F5F7` | testo |
| `TextMuted` | `#FFA1A1AA` | testo secondario |
| `TextDisabled` | `#FF71717A` | testo disabilitato |
| `Accent` / `AccentText` | `#FFF5F5F7` / `#FF121216` | pulsante primario (bianco, testo scuro) |
| `Toggle` | `#FF22C55E` | interruttore acceso |
| `Focus` | `#FF7AA2F7` | contorno di focus da tastiera |
| `BrandClaude` / `BrandCodex` | `#FFD97757` / `#FF10A37F` | cerchio dell'icona e alone della card dell'agente |
| `Success` / `SuccessText` | `#FF4ADE80` / `#FF86EFAC` | al lavoro, quota normale, hook installati |
| `Warning` / `WarningText` | `#FFFBBF24` / `#FFFCD34D` | attende input, quota 50–80 %, dati non aggiornati |
| `Danger` / `DangerText` | `#FFF87171` / `#FFFCA5A5` | errore, quota oltre 80 % |
| `Idle` | `#FF71717A` | sessione finita, anelli spenti |

Le coppie testo/sfondo sono vincolate dai test (sezione 9): testo normale ≥ 4,5:1, testo grande e grafica ≥ 3:1.

Gradienti delle barre per tono (da sinistra a destra):

| Tono | Gradiente |
|---|---|
| Normale | `#22C55E` → `#86EFAC` |
| Attenzione | `#F59E0B` → `#FCD34D` |
| Critico | `#EF4444` → `#FCA5A5` |
| Non aggiornato | `#52525B` → `#71717A` |

Il tono segue le regole di oggi: `Severity` della finestra quando lo snapshot è `Ok`, "non aggiornato" altrimenti.

### 4.2 Tipografia

- Numeri grandi e titoli: `Segoe UI Variable Display` (ripiego `Segoe UI` su Windows 10), semibold.
- Resto: `Segoe UI Variable Text`.
- Scala: 38 (numero principale del notch), 30 (titolo Impostazioni), 20 (costo nella card), 16–15 (nomi), 13
  (corpo), 11–10,5 (didascalie). Numeri con cifre tabulari dove si allineano in colonna.

### 4.3 Forme

Raggi: pannello del notch 24 (angoli sinistri), card 18, riquadri e righe 14, campi 10, pillole e pulsanti 999 (forma
a capsula). Spaziatura di base 4 px; padding delle card 16 (notch) e 18 (Impostazioni). Nessuna ombra
sull'interfaccia del notch oltre a quella del pannello: gli aloni sono gradienti radiali, non effetti.

## 5. Movimento

| Animazione | Dove | Dettagli |
|---|---|---|
| Riempimento delle barre | notch, Impostazioni | 300 ms, ease-out, dal valore precedente al nuovo; nessuna animazione al primo disegno. |
| Numeri che scorrono | numero principale e costo della card | 400 ms dal valore precedente al nuovo, solo se cambia la cifra mostrata. |
| Riflesso sulle barre | barre del notch | una banda chiara che attraversa la parte piena ogni 2,8 s. |
| Pulsazione | avatar e pallini delle sessioni al lavoro, stato nella linguetta | anello che si allarga e sfuma, 2 s. |
| Apertura del pannello | notch | scorrimento con molla leggera (`BackEase`, ampiezza 0,3) e dissolvenza, 260 ms; chiusura 160 ms ease-in. |
| Rotazione ⟳ | intestazione card | come oggi, durante il refresh. |
| Cambio scheda | Impostazioni | dissolvenza del contenuto, 150 ms. |

Regole:

- **Animazioni di Windows disattivate** (`SystemParameters.ClientAreaAnimation` falso): niente animazioni né cicli; i
  valori cambiano subito. Il valore si rilegge quando Windows lo cambia, senza riavviare.
- **Consumo:** riflesso e pulsazioni del pannello girano solo mentre il pannello è aperto; i cicli usano 30
  fotogrammi al secondo (`Timeline.DesiredFrameRate`). La linguetta tiene solo la pulsazione dello stato.

## 6. Notch

### 6.1 Linguetta chiusa

- Pillola sul bordo destro: larghezza 42 px (34 in modalità compatta), raggio 21, sfondo `NotchBackground`, bordo
  `NotchBorder`.
- Per ogni agente abilitato, un cerchio da 26 px (20 in compatta) nel colore dell'agente con l'icona bianca.
- Intorno al cerchio un anello da 2 px mostra la **prima finestra** di quota dell'agente, nel colore del suo tono;
  senza dati l'anello è spento (`Idle` al 30 %).
- In basso a destra del cerchio un pallino da 9 px con lo stato aggregato dell'agente (colori di 6.4), bordato del
  colore della linguetta; pulsa quando è "al lavoro"; assente senza sessioni.
- Altezza per agente 44 px (36 in compatta). Hover apre il pannello, click fissa: come oggi.

### 6.2 Pannello

- Larghezza 320, raggio 24 a sinistra, sfondo `NotchBackground`, bordo `NotchBorder`, ombra morbida.
- Intestazione: "AI Usage" (15, semibold) e a destra una pillola di sintesi, scelta così:
  1. almeno una sessione attende input → ambra, "1 attende input" / "N attendono input";
  2. altrimenti almeno una in errore → rossa, "1 in errore" / "N in errore";
  3. altrimenti almeno una al lavoro → verde, "N al lavoro";
  4. altrimenti nessuna pillola.
  Quando il pannello è fissato compare l'icona della puntina (SVG, non emoji) accanto alla pillola.
- Sotto, una card per agente abilitato, con scorrimento verticale quando supera l'80 % dello schermo (come oggi).

### 6.3 Card dell'agente

Dall'alto:

1. **Intestazione.** Cerchio da 34 px nel colore dell'agente con l'icona; nome (15, semibold) e sotto il piano (11,
   `TextMuted`); a destra il pulsante ⟳ rotondo da 32 px (comportamento invariato: tooltip, cooldown, attenuazione).
2. **Finestra principale.** La prima finestra dello snapshot in grande: percentuale da 38 px con "%" più piccolo in
   `TextMuted`, didascalia "finestra 5h · reset tra 3h 5m" (etichetta e countdown della finestra). Il numero è
   bianco; il tono si vede nell'anello della linguetta e nella barra sottile sotto il numero (3 px, larga quanto il
   blocco, riempita al valore).
   A destra, se i costi sono visibili: "≈ 99,60 €" (20, semibold, "≈"/"≥" in `TextMuted`) e sotto "costo API
   equivalente"; tooltip come oggi. Con costo non disponibile: "costo n/d".
   Senza finestre (nessun dato): al posto del numero "—" e la didascalia con il messaggio di stato.
3. **Altre finestre.** Una riga per finestra: etichetta a sinistra, percentuale a destra (12, semibold), barra da
   8 px con il gradiente del tono e il riflesso, didascalia "reset tra …".
4. **Messaggi.**
   - Dati non aggiornati o token scaduto: pillola ambra con il messaggio di oggi ("Ultimo aggiornamento 01:23", "Apri
     Claude Code per rinnovare la sessione").
   - Hook non installati: pillola "Stato live non attivo" con il pulsante "Installa" (stesso comando di oggi).
   - Extra usage: riga in `TextMuted` come oggi.
5. **Sessioni.** Un riquadro per sessione (raggio 14, sfondo `Tile`, padding 10):
   - **Avatar** da 30 px con l'iniziale del nome della sessione (prima lettera o cifra, maiuscola; altrimenti "•"),
     sfondo del colore di fase al 14 %, lettera nel colore di testo della fase, anello da 2 px nel colore di fase;
     pulsa quando è al lavoro.
   - **Nome** (13, semibold): resta il collegamento "porta in primo piano il terminale" con gli stessi handler e lo
     stesso tooltip di oggi.
   - **Sottotitolo** (11, colore di testo della fase): etichetta di fase di oggi e tempo dall'ultimo evento, per
     esempio "al lavoro · 2 agenti · 1m", "attende input · 4m", "finito · 12m".
   - **A destra:** costo della sessione (13, semibold, stesso testo breve di oggi) e sotto i token "↑ 21,6M ↓ 278,6k"
     (10,5, `TextMuted`); tooltip dei token come oggi. Con i costi nascosti restano solo i token.
   - **Agenti di workflow**, se ce ne sono in corso: pillola sotto il riquadro "2 agenti attivi · 350k tok · ≈ 0,80 €"
     con la freccia ▾/▸; il click apre o chiude l'elenco (stato per sessione come oggi). Ogni agente è una riga
     rientrata con pallino di fase, nome, modello, tempo, token e costo, nello stile ridotto del riquadro.
   - Nessuna sessione: "nessuna sessione attiva" (12, `TextMuted`).
6. **Alone.** Sfondo della card: `Card` più un gradiente radiale del colore dell'agente al 30 % nell'angolo in alto
   a sinistra (circa 160×130 px), che svanisce a zero.

### 6.4 Colori di fase

| Fase | Colore (anello, pallino) | Colore del testo |
|---|---|---|
| Al lavoro | `Success` | `SuccessText` |
| Attende input | `Warning` | `WarningText` |
| Errore | `Danger` | `DangerText` |
| Finito / pronto | `Idle` | `TextMuted` |

### 6.5 Modalità compatta

Linguetta come in 6.1; nel pannello il numero principale scende a 30 px, gli avatar a 24 px, i padding delle card a
12 e dei riquadri a 8. Il resto non cambia.

## 7. Impostazioni

### 7.1 Finestra

- Larghezza 760, altezza 620, ridimensionabile con minimo 700×520; sfondo `WindowBackground`; barra del titolo
  scura di sistema come oggi, titolo "AIUsageMonitor · Impostazioni".
- In alto: "Impostazioni" (30, Display semibold) e a destra la versione ("Versione 0.3.0") con lo stato
  dell'aggiornamento in `SuccessText` quando è aggiornata.
- Sotto: le **schede segmentate** in un contenitore a capsula (`Tile`): Agenti, Notch, Hook, Notifiche, Costi,
  Aggiornamenti, Sistema. La scheda attiva ha sfondo `Surface` e testo `TextPrimary`, le altre `TextMuted`.
  Tastiera: Tab raggiunge le schede, frecce sinistra/destra le scorrono (comportamento di `TabControl`).
- Il contenuto di ogni scheda scorre verticalmente se non entra.
- In fondo, sempre visibili: "Annulla" (capsula `Surface`) e "Salva" (capsula `Accent`). Salva resta disabilitato
  quando il tasso di riserva non è valido, come oggi; Invio salva, Esc annulla.
- `--updates` e la notifica di aggiornamento aprono direttamente la scheda **Aggiornamenti** (oggi scorrono al
  gruppo).

### 7.2 Controlli

- **Interruttore** al posto di ogni casella di controllo: 44×26, pomello bianco da 20, acceso `Toggle`, spento
  `SurfacePressed`; animazione del pomello 150 ms. L'etichetta sta a sinistra, l'interruttore a destra della riga.
- **Stepper** (`NumberStepper`) per i valori interi: pulsanti − e + rotondi da 36 px e il valore al centro (28,
  Display) con l'unità; limiti e passo per campo (7.3); tenere premuto ripete; il valore si può anche scrivere.
- **Campi di testo e ComboBox**: altezza 36, raggio 10, sfondo `Surface`, bordo `Hairline`, focus con bordo `Focus`.
- **Pulsanti**: capsule da 32 px (secondari `Surface`, primario `Accent`).
- **Card**: `Card`, raggio 18, padding 18, bordo `Hairline`; righe interne separate da 12 px o da una linea
  `Hairline`.

### 7.3 Contenuto delle schede

Stessi dati e comandi di oggi, riorganizzati:

- **Agenti** — due card affiancate, Claude Code e Codex, ciascuna con cerchio dell'agente, nome, piano (dall'ultimo
  snapshot, se c'è), interruttore "Monitora", stepper "Aggiorna la quota ogni" (Claude 30–600 s, passo 10; Codex
  10–600 s, passo 10). Solo Codex: interruttore "Thread figli dai rollout" con il tooltip di oggi.
- **Notch** — una card: Monitor (ComboBox), Offset verticale (stepper −5000…5000 px, passo 10), Ritardo di chiusura
  (stepper 0…5000 ms, passo 50), interruttore "Modalità compatta".
- **Hook** — una card per agente: stato con pallino colorato (installati `Success`, parziali `Warning`, non installati
  `Idle`, configurazione non valida `Danger`) e testo di oggi; pulsanti Installa e Rimuovi. Sotto: percorso del file
  eventi e "Apri cartella".
- **Notifiche** — card "Quando": Attende input, Turno completato, Errore. Card "Da": Claude Code, Codex.
- **Costi** — card: interruttore "Mostra costi (prezzi API di listino)", campo del tasso di riserva con il messaggio
  d'errore di oggi sotto, riquadro di stato (listino, tasso, ultimo errore), "Apri cartella dati".
- **Aggiornamenti** — card di stato (versione, account GitHub, ultimo controllo, stato), messaggi e avanzamento come
  oggi (la barra usa lo stile di 7.2), pulsanti in capsula (stessi comandi e visibilità), note della release
  espandibili, interruttore "Controlla automaticamente gli aggiornamenti".
- **Sistema** — interruttore "Avvio automatico con Windows", "Apri cartella log".

## 8. Menu della tray e finestra di aggiornamento

- **Menu della tray:** sfondo `WindowBackground`, raggio 14, bordo `NotchBorder`, padding 6; voci alte 32 con raggio
  10, hover `SurfaceHover`, spunta in `Toggle`; la voce "Aggiorna alla versione X…", quando c'è, è una capsula
  `Accent`. Sottomenu con la stessa pelle.
- **Aggiornamento disponibile:** titolo 17 semibold, testi come oggi, barra di avanzamento nello stile delle barre del
  notch (tono normale, riflesso), errori in `WarningText`, pulsanti "Più tardi" e conferma in capsula.

## 9. Componenti e codice

- **`Assets/Theme.xaml`**: palette di 4.1, gradienti di tono, raggi e dimensioni come risorse.
- **`Assets/Controls.xaml`**: stili impliciti ristilizzati (Button, CheckBox come interruttore, TextBox, ComboBox,
  ProgressBar, ScrollBar, Expander, TabControl/TabItem segmentati) più gli stili a chiave (`PrimaryButton`,
  `PillButton`, `IconButton`).
- **`App/Controls/UsageBar`** (controllo): barra con valore 0–100, tono, riflesso opzionale; anima il riempimento.
- **`App/Controls/NumberStepper`** (controllo): stepper intero con minimo, massimo, passo e unità.
- **`App/Controls/Motion`** (classe statica e proprietà associate): stato "animazioni attive" da
  `SystemParameters`, numeri che scorrono su un `TextBlock`, avvio e arresto dei cicli legati alla visibilità del
  pannello.
- **Notch:** `NotchWindow.xaml` e `NotchTemplates.xaml` riscritti; i view model ricevono solo proprietà di
  presentazione (pennello e alone dell'agente, finestra principale e altre finestre, iniziale e colori di fase della
  sessione, pillola di sintesi).
- **Regole di presentazione nel Core** (`AIUsageMonitor.Core/Presentation`), funzioni pure e testate: quale finestra è
  la principale e quali le altre; tono di una finestra; iniziale dell'avatar; sottotitolo della sessione; pillola di
  sintesi del pannello.
- **Impostazioni:** `SettingsWindow.xaml` riscritta con `TabControl`; `SettingsViewModel` invariato salvo la scheda
  selezionata e il piano per agente; `--updates` seleziona la scheda.
- **Tray e aggiornamento:** `TrayMenu.xaml` e `UpdatePromptWindow.xaml` ristilizzati, nessun cambio di logica.

## 10. Test e verifica

- `ThemePaletteTests` e `ColorContrastTests` aggiornati alla nuova palette: `TextPrimary` e `TextMuted` su
  `WindowBackground`, `Card`, `Surface` e sul composito `Tile` su `Card` ≥ 4,5:1; i testi di fase (`SuccessText`,
  `WarningText`, `DangerText`) su `Card` e su `Tile` ≥ 4,5:1; `AccentText` su `Accent` ≥ 4,5:1; `TextDisabled` su
  `Surface` ≥ 3:1; colori di fase e toni delle barre su `Card` ≥ 3:1.
- Unit test delle regole di presentazione (9): finestra principale con 0, 1 e più finestre; toni per severità e stato;
  iniziali (lettere, cifre, emoji, stringa vuota); sottotitoli per ogni fase con e senza agenti; pillola con tutte
  le combinazioni di fasi.
- Build senza avvisi e suite complete verdi.
- Verifica sull'app reale: UI Automation (testi, nomi dei pulsanti, schede, interruttori) e, a schermo sbloccato,
  screenshot di linguetta, pannello, ogni scheda delle Impostazioni, menu della tray e finestra di aggiornamento,
  confrontati con il mockup; controllo con le animazioni di Windows disattivate.

## 11. Rischi

- **CPU delle animazioni** in una finestra sempre in primo piano: cicli solo a pannello aperto e a 30 fps; si misura
  il consumo a riposo prima e dopo (obiettivo: nessun aumento percepibile a pannello chiuso).
- **Windows 10:** senza `Segoe UI Variable` il ripiego è `Segoe UI`; le misure restano corrette.
- **Testi lunghi** (nomi di sessione, messaggi): ellissi sul nome, a capo per i messaggi, come oggi.
- **Accessibilità:** gli interruttori restano `CheckBox` (nome e stato letti da UI Automation), le schede restano
  `TabItem`; i nomi dei pulsanti icona (⟳, stepper) sono impostati con `AutomationProperties.Name`.
