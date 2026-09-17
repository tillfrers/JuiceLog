# JuiceLog

Sammelt alle 5 Minuten Zählerstände (Strom-Smartmeter per REST, Gaszähler per Kamera) und schreibt sie in eine
PostgreSQL-Datenbank. Die Visualisierung (Grafana) ist nicht Teil des Projekts.

## Gaszähler per Kamera

Der Gaszähler hat ein mechanisches Rollenzählwerk. Eine Webcam filmt das Zählwerk, alle 5 Minuten wird
ein Einzelbild aus dem RTSP-Stream geholt und die Ziffern werden mit einem kleinen CNN gelesen:

```
RTSP-Stream --ffmpeg--> JPEG --ImageSharp--> Ziffern-ROIs (20x32 px) --TFLite-CNN--> Rollenposition pro Ziffer (z.B. 7.4)
    --> Übergangslogik (RollingDigitEvaluator) --> Zählerstand --> Plausibilitätsprüfung --> DB
```

* **Modelle**: die Ziffernmodelle des [AI-on-the-edge](https://github.com/jomjol/AI-on-the-edge-device)-Projekts
  (`Models/*.tflite`, jeweils ~300 KB). Standard ist `dig-cont_0900_s3_q.tflite`, das zusätzlich eine
  brauchbare Konfidenz liefert; `dig-class100-0182-s2_q.tflite` liegt als Alternative bei.
  Die Modelle sind vom Autor ohne freie Lizenz veröffentlicht ("copyright belongs to author, contact for
  commercial usage") – für den privaten Gebrauch unkritisch.
* **Laufzeit**: `Recognition/TfLite/TfLiteModel.cs` ist ein kleiner, rein verwalteter TFLite-Interpreter
  (nur die Ops, die diese CNNs brauchen). Dadurch gibt es keine nativen Abhängigkeiten – läuft auf dem
  Raspberry Pi (arm64, prinzipiell auch arm32) genauso wie unter Windows. Eine Auswertung (7 Ziffern) dauert auf einem PC
  ~120 ms inkl. JPEG-Dekodierung.
* **Übergänge**: Rollenzählwerke stehen oft "zwischen" zwei Ziffern. Die Logik aus der AI-on-the-edge-Firmware
  (`PointerEvalHybridNew`) ist in `RollingDigitEvaluator` nachgebaut, ergänzt um eine Korrektur für
  Kaskaden (…8 | 9.9 | 0.3 → …9.000).
* **Plausibilität** (`MeterReadingValidator`): ein Zähler läuft nie rückwärts und nicht schneller als
  `MaxIncreasePerHour`. Aus dem letzten gespeicherten Messwert, der seitdem vergangenen Zeit und `MaxIncreasePerHour`
  ergibt sich der Bereich, in dem der Zähler jetzt stehen kann (bei 5 min Abstand und 4 m³/h: letzter Wert − 0,02 bis
  letzter Wert + 0,33). Nur Werte in diesem Bereich werden gespeichert; liegt ein Wert bis zu 2 Einheiten der letzten
  Ziffer unter dem gespeicherten (Zittern der letzten Rolle bei stehendem Zähler), wird stattdessen der letzte Wert
  erneut gespeichert. Die letzte Rolle wird dafür gerundet statt abgeschnitten, damit das Rauschen symmetrisch bleibt.
* **Korrektur einzelner Rollen**: liegt der gelesene Wert außerhalb des Bereichs, ist meist eine einzelne Rolle falsch
  gelesen (die unscharfe 4 ganz links als 7, eine 9 im Schatten als 0, eine 5 als 6). Zwei Korrekturen sind erlaubt:
  * *Vordere Rollen*, die im ganzen Bereich dieselbe Ziffer zeigen, können sich seit dem letzten Wert nicht gedreht
    haben - dort wird die bekannte Ziffer eingesetzt, egal was das Netz gelesen hat (bei 5 min Abstand meist alle
    Rollen bis zur Einerstelle, nach 2 h die ersten drei). Die Konfidenz dieser Rollen spielt keine Rolle mehr; weichen
    dort **mehrere** Rollen ab, hat sich vermutlich die Kamera bewegt und der Snapshot wird verworfen.
  * *Eine Nachbarziffer*: passt der Wert danach immer noch nicht, wird für jede übrige Rolle geprüft, ob die Ziffer
    davor oder dahinter auf der Rolle (0↔9, 5↔6, …) einen plausiblen Wert ergibt. Von mehreren Möglichkeiten gewinnt
    der **kleinste** Wert: ein zu kleiner Wert wird von der nächsten Messung eingeholt, ein zu großer bleibt für immer
    stehen, weil danach jeder richtige Wert als "rückwärts" abgelehnt würde.

  Jede Korrektur steht im Log (`Camera: 4375027 corrected to 4375927 (digit #4 0->9): the meter must show
  43758.94..43760.62 now`). Für die übrigen Rollen gilt `MinConfidence`: ist das Netz sich dort unsicher, ist der
  Snapshot unbrauchbar.
* **Mehrere Snapshots pro Lauf**: pro Durchlauf werden bis zu drei Snapshots gelesen; sobald zwei davon übereinstimmen,
  ist Schluss. Von mehreren plausiblen Werten wird der kleinste gespeichert (Begründung wie oben). Ein unbrauchbarer
  Snapshot (Kamera nicht erreichbar, Ziffer unsicher, unplausibel) wird durch einen frischen ersetzt.
* **Lückenlose Reihe**: war kein Snapshot des Laufs brauchbar, wird der letzte Messwert erneut gespeichert, in der
  Spalte `Estimated` als Schätzung markiert (`RepeatLastValueWhenUnreadable`, Standard an). Der Zähler läuft nie
  rückwärts, der letzte Messwert ist also eine sichere Untergrenze. Der Plausibilitätsbereich rechnet weiter ab dem
  letzten **echten** Messwert, das Zeitfenster wächst also, solange nichts gelesen werden kann. Für einen Grafana-Alarm
  "Kamera liest nicht" eignet sich deshalb `Estimated = true` über längere Zeit statt "keine Daten".
* **Zählerstand von Hand setzen**: hat sich doch einmal ein falscher Wert festgesetzt, kann der am Zähler abgelesene
  Stand auf der Kalibrierseite unter *Zählerstand* eingetragen werden. Er wird wie eine Messung gespeichert und ist ab
  dann die Referenz. Falsche Werte in der Vergangenheit bleiben davon unberührt, die lassen sich nur direkt in der
  Datenbank korrigieren (`UPDATE "Energy" SET "Value" = "Value" - 1 WHERE "LoggerType" = 1 AND "Date" BETWEEN … AND …`).

### Konfiguration (`appsettings.json`)

```json
{
  "LoggerType": 1,
  "EnergyType": 1,
  "Url": "192.168.178.136:554/stream1",
  "User": "…",
  "Password": "…",
  "Camera": {
    "ModelPath": "Models/dig-cont_0900_s3_q.tflite",
    "DecimalDigits": 2,          // wie viele der hinteren ROIs Nachkommastellen sind
    "MinConfidence": 0.6,        // unsichere Ziffer (außer bekannte vordere Rollen) -> Snapshot unbrauchbar
    "MaxIncreasePerHour": 3.5,   // m³/h, Obergrenze für die Plausibilitätsprüfung (26-kW-Brennwerttherme: ~2,4-2,8 bei Volllast)
    "AutoContrast": true,        // Kontrast pro Ziffer strecken (wichtig bei IR-Bildern)
    "RepeatLastValueWhenUnreadable": true, // kein brauchbarer Snapshot -> letzten Messwert als Schätzung erneut speichern
    "DebugDirectory": "debug",   // relativ zum Programmordner; leer = aus
    "DigitRois": [ { "X": 483, "Y": 465, "Width": 64, "Height": 100 }, … ]
  }
}
```

`Ffmpeg:BinaryFolder` bleibt leer, wenn `ffmpeg` im PATH bzw. im Programmordner liegt (Pi: `apt install ffmpeg`).

### ROIs kalibrieren

Die Kamera ist fest montiert, deshalb werden die Ziffern über feste Pixel-Rechtecke (`DigitRois`, von links
nach rechts) ausgeschnitten.

**Kalibrierseite:** Solange keine `DigitRois` konfiguriert sind, überspringt der Job die Kamera und verweist auf
`http://<host>:47311/` (Port über `Calibration:Port`, abschaltbar mit `Calibration:Enabled: false`; auf einem Rechner mit Desktop wird die Seite dann automatisch im Browser geöffnet, `Calibration:OpenBrowser: false` verhindert das). Die Seite zeigt
einen frischen Snapshot; dort zieht man mit der Maus ein Kästchen um jede Ziffernrolle (verschieben, an der Ecke
skalieren, Pfeiltasten für Feinjustage), stellt die Nachkommastellen ein und lässt mit **Kästchen optimieren**
Position und Größe automatisch nachjustieren (zu großzügige Kästchen lassen die nächste Ziffer mit ins Bild, das
Netz liest dann z. B. "5.8" statt "5.0"). **Testen** zeigt die 20x32-Crops, Rohwert und Konfidenz je Ziffer sowie
den Zählerstand; **Speichern** schreibt die ROIs in die `appsettings*.json` **neben dem laufenden Programm**
(beim Start aus der IDE also `bin/Debug/net10.0/`) und zusätzlich in die Projektdatei, wenn das Programm aus einem
Build-Ordner unterhalb der `.csproj` läuft. Sie werden ohne Neustart beim nächsten Durchlauf verwendet.
Die Seite bleibt auch danach erreichbar: beim Öffnen lädt sie die gespeicherten Kästchen (Zoom "Anpassen" zeigt
das ganze Bild), die sich nachjustieren und erneut speichern lassen (etwa nach einem Kamerastoß). Sie hat keine Anmeldung –
nur im Heimnetz betreiben. Unter Windows braucht `http://*:47311/` einmalig
`netsh http add urlacl url=http://*:47311/ user=Everyone` (sonst nur `localhost`), unter Linux nicht.

Zusätzlich liegen nach jedem Lauf im `DebugDirectory`:

* `last_frame.jpg` – der Snapshot mit den ROIs als rote Rahmen,
* `digit_N.png` – die 20x32-Ausschnitte, die das Netz tatsächlich sieht.

Faustregeln (Stand 09/2026, 2304x1296-Bild): ROI-Seitenverhältnis ≈ 20:32, die Ziffer mittig mit
~10 px Rand oben/unten und ~12 px links/rechts. Eine ruhende Ziffer sollte im Log als `x.0` (±0.1) gelesen
werden; liest das Netz dauerhaft `x.2`, sitzt der ROI zu tief bzw. hoch. Die Konsolenausgabe zeigt pro
Ziffer Rohwert und Konfidenz:

```
Camera: raw [4.0 3.0 7.0 5.0 8.0 2.0 7.0] confidence [1.00 1.00 1.00 1.00 1.00 1.00 1.00] -> 4375827
```

Die Modelle werden mit ins Ausgabeverzeichnis kopiert. Auf dem Pi wird nur `ffmpeg` zusätzlich benötigt.
