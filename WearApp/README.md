# CardboardXR Pinch (Wear OS / Pixel Watch)

App companion per **Pixel Watch** (Wear OS 3.0+) che rileva il **doppio pinch** tramite accelerometro e giroscopio a 200 Hz e lo invia a CardboardXR sul telefono tramite **Wearable Data Layer API**.

## Funzionamento

1. **Rilevamento inerziale sul polso (`DoublePinchDetector.kt`):**
   - Filtro passa-alto a 8 Hz sull'accelerometro per rimuovere gravità e movimenti lenti del braccio.
   - Rilevamento di due impulsi ravvicinati (100–550 ms) preceduti da una finestra di quiete e senza un terzo impulso entro 220 ms (evita falsi positivi da camminata o tamburellamento).
   - **Veto del giroscopio:** se il polso sta ruotando (> 1.2 rad/s) o ha appena subito un urto forte, il rilevamento viene inibito per 250 ms.
   - Ogni doppio pinch confermato produce un feedback aptico (`EFFECT_CLICK`) sull'orologio e invia `/cardboardxr/pinch` al telefono.
2. **Servizio in background (`PinchService.kt`):**
   - Usa un `ForegroundService` con `PARTIAL_WAKE_LOCK` per continuare a leggere i sensori a 200 Hz anche quando lo schermo del Pixel Watch va in standby / Always-On.
   - Si arresta automaticamente dopo 10 minuti se l'app sul telefono non risponde agli heartbeat.
3. **Integrazione automatica con CardboardXR sul telefono (`WatchLink.cs` / `PinchRouter.cs`):**
   - Quando l'app sul Pixel Watch è attiva e connessa, il **doppio pinch dell'orologio sostituisce il pinch visivo della fotocamera** (Lobby, 360 Video, Orb Rush, Portal) e in 3DoF spegne la fotocamera risparmiando batteria e calore.
   - Quando entri in una scena che richiede l'interazione spaziale con la mano (**Air Paint** per disegnare o **Cube Sandbox** per afferrare e spostare oggetti in AR, dove `DemoRequirements.HandPinch = true`), il **pinch normale tramite riconoscimento visivo viene riattivato automaticamente**, mentre il doppio pinch dell'orologio continua comunque a funzionare per cliccare i pulsanti UI.
4. **Controlli sullo schermo dell'orologio (`MainActivity.kt`):**
   - **Avvia / Ferma**: attiva o ferma la sessione VR.
   - **Barra movimento e Sensibilità (`-` / `+`, da 1 a 10)**: mostra in tempo reale l'ampiezza degli impulsi rispetto alla soglia (posta a metà barra).
   - **Click**: pulsante di fallback sullo schermo dell'orologio.
   - **Impostazioni VR**: apre il pannello impostazioni dentro CardboardXR sul telefono.

## Requisito importante: Stessa firma (Keystore)

La **Wearable Data Layer API** di Google Play Services consegna i messaggi tra telefono e orologio **solo se entrambe le app**:
1. Hanno lo stesso `applicationId` (`com.brenpoly.cardboardxr`).
2. Sono **firmate con la stessa chiave** (stesso `debug.keystore` in sviluppo, oppure stesso keystore di release).

## Build e installazione sul Pixel Watch

Puoi aprire la cartella `WearApp` direttamente in **Android Studio** oppure compilare da riga di comando:

```powershell
cd WearApp
.\gradlew.bat assembleDebug
adb -s <SERIALE_PIXEL_WATCH> install -r app\build\outputs\apk\debug\app-debug.apk
```
