# ParkManager – technische Produktspezifikation

Status: fortgeschriebener Arbeitsplan, Stand 20. September 2026  
Zielplattform: Cities: Skylines II  
Lizenzannahme: GPL-3.0  

## Ziel

ParkManager ist ein generischer, interaktiver Parkgenerator für Cities: Skylines II. Der Spieler zeichnet ein beliebiges einfaches Polygon, setzt oder bestätigt Zugänge und erzeugt darin einen editierbaren Park aus echten internen Spielobjekten: begehbare Wege, Oberflächen, Vegetation, Bänke, Laternen, Mülleimer, Zäune und optionale Mittelpunktobjekte.

Der Generator soll zwei gleich wichtige Arbeitsweisen unterstützen:

1. **Schnell erzeugen:** Polygon zeichnen, Stil wählen, Variante würfeln, bauen.
2. **Geführt entwerfen:** Eingänge, Hauptachse, Attraktionsanker, Ausschlussbereiche und einzelne Parameter vorgeben; der Generator ergänzt den Rest.

Die Ausgabe muss reproduzierbar, performant, vollständig löschbar und nach dem Bau erneut editierbar sein. Sämtliche Assets stammen aus dem Basisspiel oder installierten offiziellen Inhalten; Version 1 liefert keine eigenen 3D-Assets.

## Annahmen

- Der Mod wird als eigenständiges Projekt auf Basis der offiziellen CS2-Code-Mod-Toolchain erstellt.
- Architektur und geeignete GPL-3.0-Komponenten des ParkingLotTool dürfen mit Attribution übernommen oder adaptiert werden. ParkManager bleibt deshalb GPL-3.0.
- ParkManager hat keine Laufzeitabhängigkeit vom ParkingLotTool.
- Der erste Release erzeugt Parks auf Land. Seen, Flüsse, Tierparks und Vergnügungsparks sind Erweiterungen, nicht Teil des MVP.
- Vanilla- und offizielle DLC-Assets werden zur Laufzeit über Prefab-Kataloge gefunden; interne Namen werden nicht als einzige dauerhafte Identität gespeichert.
- Ein Park ist zunächst eine gestaltete und begehbare Anlage. Eine eigene Parkwirtschaft, Eintrittspreise, Personal, Besucherbedürfnisse oder neue Simulationsmechaniken sind nicht Teil von Version 1.
- Generierung ist deterministisch: gleicher Polygonzustand, gleiche Konfiguration, gleicher Asset-Katalog und gleicher Seed ergeben denselben Plan.

## Nicht-Ziele

- Keine eigenen Modelle, Texturen, Sounds oder Animationen in Version 1.
- Kein vollwertiges Landschaftsarchitektur-CAD und keine freie Bearbeitung jedes einzelnen generierten Props im ParkManager-Panel.
- Keine generative KI und keine Cloud-Abhängigkeit.
- Keine exakte botanische oder ökologische Simulation.
- Keine automatische Geländeformung im MVP; Gelände wird analysiert und berücksichtigt, aber nicht irreversibel umgebaut.
- Keine Abhängigkeit von proprietären CityEngine-, Houdini- oder ArcGIS-Laufzeiten.
- Kein direktes Portieren des Python-Generators aus 3DScenePlatform in den Spielprozess.
- Kein Anspruch, jedes konkave oder degenerierte Polygon sinnvoll zu bebauen. Ungeeignete Eingaben werden verständlich abgelehnt.

## Nutzerablauf

### Neuer Park

1. ParkManager-Werkzeug öffnen.
2. Polygonpunkte setzen; optional an Straßen, Wegen, Grundstückskanten oder vorhandenen Punkten einrasten.
3. Polygon schließen und Punkte/Kanten nachbearbeiten.
4. Automatisch erkannte Zugangskandidaten prüfen; Zugänge hinzufügen, verschieben oder löschen.
5. Stil-Preset wählen.
6. Optional Attraktionsanker, Hauptachse, Sperrflächen oder gewünschte Randsegmente setzen.
7. Vorschau erzeugen und Varianten über den Seed durchschalten.
8. Warnungen und geschätzte Objektzahlen prüfen.
9. Park bauen.

### Bestehenden Park bearbeiten

1. Park über seinen logischen `ParkRecord` als Einheit auswählen; einzelne
   erzeugte Vanilla-Elemente bleiben zusätzlich direkt auswählbar.
2. „Park bearbeiten“ öffnen.
3. Gespeichertes Polygon, Marker, Optionen und Seed laden.
4. Neue Vorschau erzeugen, ohne den bestehenden Park zu verändern.
5. Änderung bestätigen: neue Version atomar bauen, danach alte Kindobjekte entfernen.
6. Abbrechen: bestehender Park bleibt unverändert.

## Projektstruktur

```text
ParkManager/
├── ParkManager.csproj
├── Mod.cs
├── Setting.cs
├── LICENSE
├── README.md
├── CHANGELOG.md
├── Properties/
│   ├── PublishConfiguration.xml
│   ├── PublishProfiles/
│   ├── Thumbnail.png
│   └── README.txt
├── Library/
│   └── Harmony/                 # nur falls benötigte Patches bestätigt sind
├── Domain/                      # reine Datentypen, keine Game-/Unity-Typen
│   ├── ParkDefinition.cs
│   ├── ParkSettings.cs
│   ├── ParkPlan.cs
│   ├── ParkStyle.cs
│   ├── Anchors.cs
│   ├── PathGraph.cs
│   ├── Regions.cs
│   ├── Placements.cs
│   └── Diagnostics.cs
├── Geometry/                    # deterministischer, headless testbarer Kern
│   ├── Polygon/
│   ├── Terrain/
│   ├── Entrances/
│   ├── Layout/
│   ├── Paths/
│   ├── Regions/
│   ├── Vegetation/
│   ├── Furniture/
│   ├── Validation/
│   └── GenerationPipeline.cs
├── Presets/
│   ├── PresetDefinitions.cs
│   ├── NaturalPreset.cs
│   ├── UrbanPreset.cs
│   ├── FormalPreset.cs
│   ├── WoodlandPreset.cs
│   └── OpenPreset.cs
├── Assets/                      # Kataloglogik, keine eigenen Spielassets
│   ├── AssetCatalog.cs
│   ├── AssetQuery.cs
│   ├── AssetClassification.cs
│   ├── AssetSelection.cs
│   └── DlcAvailability.cs
├── Tools/                       # CS2-Werkzeug und Weltinteraktion
│   ├── ParkToolSystem.cs
│   ├── ParkRaycast.cs
│   ├── ParkSnapping.cs
│   ├── ParkPolygonEditing.cs
│   ├── ParkAnchorEditing.cs
│   ├── ParkPreviewSystem.cs
│   ├── ParkBuildSystem.cs
│   ├── ParkEditSystem.cs
│   ├── ParkOwnerSystem.cs
│   ├── ParkBulldozeSystem.cs
│   └── ParkUndoSystem.cs
├── ECS/
│   ├── Components/              # ParkRecord, Member, Definition, Version, Tags
│   ├── Serialization/
│   ├── Maintenance/
│   └── Cleanup/
├── Placement/                   # ParkPlan -> CS2-Entities
│   ├── PathPlacement.cs
│   ├── SurfacePlacement.cs
│   ├── PlantPlacement.cs
│   ├── PropPlacement.cs
│   ├── FencePlacement.cs
│   └── PlacementReceipt.cs
├── UI/
│   ├── package.json
│   ├── webpack.config.js
│   └── src/
│       ├── index.tsx
│       ├── bindings.ts
│       ├── panel.tsx
│       ├── sections/
│       ├── controls/
│       ├── asset-picker/
│       └── localization/
├── Localization/
│   ├── en-US.cs
│   └── de-DE.cs
└── Tests/
    ├── Geometry/
    ├── GoldenCases/
    ├── PropertyTests/
    ├── Determinism/
    ├── Performance/
    └── GameParity/
```

### Schichtengrenzen

- `Domain` und `Geometry` referenzieren weder `Game.*`, `Unity.Entities` noch Unity-Rendering.
- Der Geometriekern arbeitet intern in lokalen 2D-Meterkoordinaten mit `double` und konvertiert erst am Adapterrand.
- `Assets` kennt CS2-Prefabs, liefert dem Kern aber nur klassifizierte Maße und Fähigkeiten.
- `Placement` kennt sowohl `ParkPlan` als auch CS2-ECS und ist der einzige reguläre Erzeugungspfad für dauerhafte Entities.
- `Tools` verwaltet Interaktion, Vorschau, Zustandsmaschine und asynchrone Berechnung.
- `UI` verändert nur eine serialisierbare Konfiguration; Geometrieentscheidungen werden nicht doppelt in TypeScript implementiert.

## Zentrales Datenmodell

### Builderfamilien

Park und Plaza sind zwei verschiedene Builder, keine Presets desselben
Generators. Gemeinsam bleiben nur Infrastruktur wie Polygonerfassung,
Assetkatalog, Vorschau, Baujournal, Auswahl und Löschen. Jeder Builder besitzt
eine eigene serialisierbare Definition, Einstellungsmenge, Validierung und
Layoutpipeline:

- `ParkBuilder` → `ParkDefinition`, `ParkPlan` und organische beziehungsweise
  formale Parkwege samt Vegetation und Ausstattung
- `PlazaBuilder` → später `PlazaDefinition`, `PlazaPlan` und eigene Regeln für
  befestigte Flächen, Platzachsen, Randzonen und Möblierung

Ein persistierter `ProceduralSiteBuilder`-Discriminator kennzeichnet den
zuständigen Builder. Alte Datensätze ohne Discriminator gelten als Park. Der
Wert `Plaza` ist bereits reserviert, löst aber noch keine Generierung aus.

### ParkDefinition

Persistierte, nutzerbestimmte Eingabe:

- stabile Park-ID und Schema-Version
- Polygon in lokalen Koordinaten plus Weltursprung
- Eingänge mit Randposition, Typ, Breite und optionaler Zielverbindung
- Hauptachse beziehungsweise Ausrichtungswinkel
- Attraktionsanker mit Typ, Position, Radius und Pflicht/optional
- Ausschlusszonen und bevorzugte Zonen
- ausgewähltes Preset plus explizite Überschreibungen
- Assetfilter und konkrete Asset-Pins
- Seed
- Generatorversion

### ParkPlan

Vergängliches, vollständig berechnetes Ergebnis:

- normalisiertes Polygon
- Geländestatistik
- Zugänge
- PathGraph mit Knoten, Kanten, Hierarchie und Breiten
- Wegkorridore als Polygone
- semantische Regionen
- Oberflächenpolygone
- Pflanzenplatzierungen
- Möblierungsplatzierungen
- Zaunsegmente und Tore
- Diagnosemeldungen, Qualitätswerte und Mengenschätzung

### Persistenzstrategie

- Dauerhaft gespeichert wird primär `ParkDefinition`, nicht nur die erzeugten Ausgabeelemente.
- Zusätzlich werden Generatorversion und ein kompaktes `PlacementReceipt` gespeichert, damit top-level Vanilla-Elemente über `ParkMember` sicher zugeordnet und entfernt werden können.
- Beim Laden wird der vorhandene Park nicht automatisch neu generiert. Dadurch verändern Spiel- oder Modupdates keine existierenden Parks.
- Eine explizite Aktion „Mit aktueller Generatorversion neu erzeugen“ erstellt erst eine Vorschau und verlangt Bestätigung.
- Assetreferenzen speichern eine logische Klassifikation und, wenn der Nutzer ein konkretes Asset gewählt hat, dessen stabile Prefab-Identität. Fehlt ein Asset, wird ein kompatibler Ersatz vorgeschlagen.

## Erstellungsphasen

### Phase 0 – Katalog und Fähigkeiten initialisieren

Benötigt:

- `PrefabSystem` und Assetdatenbank
- verfügbare interne Baum-, Busch-, Blumen-, Prop-, Zaun-, Laternen-, Oberflächen- und Wegprefabs
- DLC-Verfügbarkeit
- Maße, Bounds, Drehachse und geeignete Platzierungsart jedes Assets

Berechnungen:

- Prefabs nach Fähigkeiten klassifizieren, nicht ausschließlich nach Namen.
- Kategorien und Tags aus Komponenten, UI-Gruppen und bekannten Fallbacktabellen ableiten.
- physische Footprints und Sicherheitsabstände bestimmen.
- ungeeignete, unsichtbare, animierte oder systeminterne Prefabs ausfiltern.

Ausgabe: unveränderlicher `AssetCatalogSnapshot`, dessen Hash in die Vorschau eingeht.

### Phase 1 – Polygon erfassen und normalisieren

Benötigt:

- mindestens drei unterschiedliche Punkte
- Terrain-Raycast pro Punkt
- Snap-Ziele und aktuelle Kamera-/Werkzeugdaten

Berechnungen:

- aufeinanderfolgende Duplikate und sehr kurze Kanten entfernen
- konsistente Orientierung herstellen
- Selbstüberschneidungen erkennen
- nahezu kollineare Punkte optional vereinfachen
- minimale Kantenlänge und minimale Fläche prüfen
- lokales Koordinatensystem um Polygonzentrum bilden
- Bounding Box, orientiertes Minimum-Bounding-Rectangle, Hauptachsen und Konkavität berechnen
- Polygon triangulieren und gegen CS2-Flächenregeln validieren

Fehlerfälle:

- Selbstüberschneidung, Nullfläche, zu kurze Kanten, unzulässige Weltgrenze
- Fläche unter Mindestgröße
- schmale Korridore, in die kein konfigurierter Weg passt

### Phase 2 – Umgebung und Gelände analysieren

Benötigt:

- Terrainhöhenraster
- bestehende Straßen, Fußwege, Gebäude, Netze, Wasser und statische Objekte im erweiterten Polygon-Bounds

Berechnungen:

- Terrain auf adaptivem Raster abtasten
- Höhe, lokale Steigung, Gefällevektor und Rauheit interpolieren
- Anteil der Fläche über Warn- und Maximalsteigung bestimmen
- Randabschnitte nach Nähe und Orientierung zu Straßen/Fußwegen bewerten
- vorhandene Hindernisse zu Ausschlusspolygonen mit Sicherheitsabstand erweitern
- Wasserüberschneidungen und Überflutungsrisiko markieren

Ausgabe: `SiteContext` mit Höhenfeld, Hindernissen, Anschlusskandidaten und Diagnosen.

MVP-Verhalten:

- Wege folgen dem Terrain.
- Kanten mit zu großer Längs- oder Querneigung werden verworfen oder umgeleitet.
- Parkbau wird blockiert, wenn keine begehbare Verbindung erzeugt werden kann.

### Phase 3 – Zugänge bestimmen

Automatik:

- Randsegmente nahe bestehenden Straßen oder Fußwegen als Kandidaten erzeugen.
- Kandidaten nach Anschlussdistanz, Winkel, Terrain, Sichtbarkeit, Abstand zu Ecken und gegenseitigem Abstand bewerten.
- Anzahl anhand Parkgröße und Preset wählen.
- mindestens einen erreichbaren Zugang verlangen.

Manuell:

- Zugang auf Rand ziehen, verschieben und löschen
- Typen: Haupteingang, Nebeneingang, nur Fußweg, zukünftiger Servicezugang
- Torbreite und Bedeutung festlegen

Wichtige Regel: Zugang wird durch geometrisches Merkmal und Randparameter gespeichert, nicht nur durch eine fragile Kantennummer. Nach Polygonbearbeitung wird er auf die nächste kompatible Randstelle projiziert.

### Phase 4 – Anker und Gestaltungsrahmen bestimmen

Ankertypen:

- zentraler Platz
- Brunnen/Statue/Denkmal
- Pavillon
- Spielplatz-/Aktionsfläche
- Lichtung
- Aussichtspunkt
- Teich/Wasserfläche, erst nach MVP
- freier Nutzeranker ohne automatisch gesetztes Asset

Berechnungen:

- Kandidaten aus Distanzfeld-Maxima, Polygon-Schwerpunkt, Voronoi-Zentren und großen flachen Restflächen bilden.
- Footprint, Sicherheitsrand, Terrain und Abstand zu Eingängen prüfen.
- Pflichtanker zuerst platzieren; optionale Anker nach Preset und Flächenbudget.
- sich überschneidende Anker mittels gewichteter Auswahl auflösen.

Gestaltungsrahmen:

- Hauptachse aus Nutzerangabe, längster geeigneter Randkante, orientiertem Rechteck oder Eingangsbeziehung bestimmen.
- Symmetrieachse beziehungsweise radiales Zentrum ableiten.
- Inset-Zonen (`Peel`) am Rand berechnen: Randweg, Baumgürtel, Zaunabstand oder freie Kante.

### Phase 5 – abstrakten PathGraph erzeugen

Der PathGraph ist zunächst topologisch. Er enthält noch keine endgültigen Kurven oder Oberflächen.

Pflichtknoten:

- alle Eingänge
- alle Pflichtanker
- Mittelpunkt bei radialen Stilen
- notwendige Übergänge zwischen getrennten begehbaren Teilflächen

Kandidatenknoten:

- Medial-Axis-/Straight-Skeleton-Punkte
- Voronoi-Knoten
- Zellzentren
- Schnittpunkte formaler Achsen
- Punkte eines optionalen Randrings

Kandidatenkanten:

- Delaunay-/Sichtbarkeitsverbindungen innerhalb des Polygons
- Skeleton-Nachbarschaften
- radiale Speichen
- konzentrische beziehungsweise polygonparallele Ringe
- Rasterachsen

Kantenkosten:

```text
Kosten =
    Länge
  + Kurvenstrafe
  + Steigungsstrafe
  + Hindernisnähe
  + Randnähe gemäß Stil
  + Kreuzungsstrafe
  + unattraktive Restflächen
  - gewünschte Sichtbeziehung
  - Verbindung zu Pflichtziel
```

Topologische Optimierung:

1. Pflichtknoten über minimalen zusammenhängenden Graph verbinden.
2. Direkte, gut nutzbare Eingang-zu-Eingang-Verbindungen bevorzugen.
3. Je nach Loop-Faktor zusätzliche Kanten aufnehmen.
4. Sackgassen ohne Ziel entfernen.
5. Knotengrad begrenzen; ungünstige nahe Kreuzungen zusammenführen.
6. Haupt- und Nebenwege anhand Betweenness/Flussabschätzung klassifizieren.
7. Erreichbarkeit sämtlicher Pflichtanker beweisen.

### Phase 6 – Layoutmodus anwenden

Layoutmodi sind Strategien, keine vollständig getrennten Generatoren.

#### Organisch/Natürlich

- Medial Axis und Sichtbarkeitsgraph als Grundlage
- wenige direkte Hauptverbindungen
- geschwungene Nebenwege
- asymmetrische Schleifen und Lichtungen
- geringe Kreuzungsdichte

#### Formal/Raster

- Hauptachse und orthogonale/diagonale Querachsen
- symmetrische Knoten- und Regionsspiegelung
- optionaler Randweg
- gerade oder nur leicht gerundete Wege

#### Radial/Sternförmig

- Mittelpunktanker erforderlich oder automatisch erzeugt
- konfigurierbare Anzahl Speichen
- optionale Verbindung der Speichen durch Ringweg
- ungeeignete sehr längliche Flächen werden gewarnt oder auf Hybrid umgestellt

#### Kreisförmig/Konzentrisch

- ein oder mehrere geschlossene Ringe um einen Mittelpunkt oder eine Lichtung
- Zugänge an den äußeren Ring anschließen
- radiale Querverbindungen nach Mindestabstand
- Ringe müssen nicht geometrisch kreisrund sein; polygonparallele Offsets sind bevorzugt

#### Zellen/Voronoi

- Seeds kontrolliert im Polygon verteilen
- geglättete Zellgrenzen erzeugen
- ausgewählte Zellgrenzen als Wege verwenden
- große Zellen als Wiese/Hain/Feature-Fläche klassifizieren

#### Hybrid

- direkte Hauptwege plus organische Nebenwege
- Standard für Stadtpark
- kann formale Eingangszone mit natürlichem Innenbereich kombinieren

#### Minimal

- nur Eingänge und Pflichtanker verbinden
- keine dekorativen Schleifen
- geeignet für kleine Parks und Performanceprofil

### Phase 7 – Wege geometrisieren

Berechnungen:

- Graphkanten zu Polylines und anschließend zu geglätteten Kurven konvertieren
- Eckpunkte mit Catmull-Rom-, Bézier- oder biarc-basierten Übergängen abrunden
- Kurvenabweichung begrenzen, damit Wege nicht aus Polygon oder freien Korridoren laufen
- Mindestkurvenradius und maximale Steigung einhalten
- Haupt-/Nebenwegbreite puffern und Wegkorridorpolygone erzeugen
- Kreuzungen vereinigen und kleine Splitter beseitigen
- Wegkorridore vom bebaubaren Parkpolygon abziehen
- Netzknoten für echte unsichtbare Fußwege bestimmen

Qualitätsprüfungen:

- keine Selbstüberschneidung des Wegkorridors
- keine unzugänglichen Pflichtziele
- keine Korridorbreite unter Sollwert
- keine zu kurzen Netzsegmente
- Kreuzungswinkel und Knotenabstände innerhalb definierter Grenzen
- Flächentriangulierung entspricht dem Verhalten von CS2

### Phase 8 – Restfläche in semantische Regionen teilen

Eingabe: Parkpolygon minus Wege, Ankerfootprints, Hindernisse und Randpuffer.

Regionsklassen im MVP:

- offene Wiese
- lockerer Baumhain
- dichter Baum-/Strauchbereich
- Blumen-/Zierbeet
- befestigter Platz
- freie/reservierte Fläche
- Randpflanzung

Berechnungen:

- zusammenhängende Restpolygone bestimmen
- sehr kleine Sliver mit passendem Nachbarn vereinigen
- große Regionen abhängig vom Stil weiter teilen (`Grid`, `Cells`, `Rays`, `Peel`)
- Regionseigenschaften ermitteln: Fläche, Kompaktheit, Randkontakt, Wegkontakt, Zentrum, Terrain, Sonnigkeitsproxy und Sichtbarkeit
- Regionstypen über gewichtete Regeln und Flächenbudgets zuweisen

Beispielbewertung:

```text
Wiese:       groß + kompakt + flach + zentral
Baumhain:    randnah + mittel/groß + nicht zu steil
Beet:        wegnah + klein/mittel + sichtbar + formal
Platz:       an Hauptknoten/Anker + flach + gut erreichbar
Dichtgrün:   randnah + weit von Hauptplatz + Sichtschutz erwünscht
```

Ein begrenzter Optimierer darf Zuordnungen verbessern, muss aber deterministisch sein und ein festes Zeit-/Iterationsbudget besitzen. Das MVP beginnt mit gewichteten Heuristiken; ein genetischer Algorithmus ist keine Voraussetzung.

### Phase 9 – Oberflächen planen

- jeder Region und jedem Weg eine interne Oberfläche zuweisen
- gleiche angrenzende Oberflächen vor dem Bau vereinigen
- Löcher und Winding korrekt behandeln
- Polygone nach CS2-kompatibler Ear-Clipping-Regel triangulieren
- Mindestkante, Mindestfläche und maximale Punktzahl einhalten
- bei ungültiger Oberfläche gezielt zerlegen statt stillschweigend verwerfen
- Oberfläche kann pro Kategorie deaktiviert werden, damit der Spieler Boden selbst bemalt

### Phase 10 – Vegetation verteilen

Für jede Region wird ein kontinuierliches Eignungsfeld definiert:

```text
Eignung(x,y) =
    Regionsbasis
  + Randpräferenz
  + Gruppenfeld
  + Stilvariation
  - Wegpuffer
  - Prop-/Ankerpuffer
  - Hindernispuffer
  - Steigungsstrafe
  - Sichtfreihaltezone
```

Platzierungsverfahren:

- variable-radius Poisson-Disk-Sampling für große Einzelpflanzen
- Clusterzentren plus lokale Blue-Noise-Samples für Baum- und Buschgruppen
- bandförmige Samples für Randpflanzung
- Beetpolygone mit dichterer, kleinerer Verteilung
- getrennte Mindestabstände je Assetklasse und Kombination
- deterministische Zufallsströme pro Region und Schicht, damit eine geänderte Bankdichte nicht sämtliche Bäume neu würfelt

Regeln:

- Bäume halten freie Sicht an Eingängen und Hauptkreuzungen.
- keine Pflanzen innerhalb der Footprints anderer Objekte.
- große Bäume erhalten größeren Weg- und Laternenabstand.
- offene Wiesen behalten einen konfigurierbaren tatsächlich freien Anteil.
- Dichte wird sowohl relativ als auch durch absolutes Objektbudget begrenzt.
- Assetmix nutzt gewichtete Listen; fehlende Assets werden kompatibel ersetzt.

### Phase 11 – Möblierung, Beleuchtung und Zaun

#### Bänke

- entlang geeigneter Wegseiten sampeln
- zum Weg tangential ausrichten, Sitzseite zur interessanteren Region
- Kreuzungen, Eingänge und andere Bänke freihalten
- erhöhte Wahrscheinlichkeit nahe Plätzen, Aussichtspunkten und Attraktionen

#### Laternen

- entlang Hauptwegen in nahezu regelmäßigem Abstand
- optional an Nebenwegen mit größerem Abstand
- an Kreuzungen deduplizieren
- Seite alternieren oder beidseitig, je Preset

#### Mülleimer und sonstige Props

- bevorzugt bei Eingängen, Bänken, Plätzen und Hauptknoten
- stochastisch, aber mit Mindestabstand und Objektbudget

#### Zaun

- vollständiger Rand, ausgewählte Randsegmente oder kein Zaun
- Tore an allen freigegebenen Eingängen
- Ecken und kurze Restsegmente geometrisch behandeln
- geschlossene Einfriedung validieren
- Zaun kann als Propkette oder geeignetes internes Netz umgesetzt werden; Entscheidung erst nach Prefab-Prototyp

### Phase 12 – Imperfection Pass

Kontrollierte Abweichungen dürfen nur ästhetische Parameter verändern, nie Erreichbarkeit oder Kollisionen:

- Wegkontrollpunkte leicht verschieben
- Abstände innerhalb eines Intervalls variieren
- Pflanzenrotation und zulässige Skalierung variieren
- Clustergrößen variieren
- einzelne optionale Props auslassen
- formale Symmetrie kann wahlweise perfekt oder leicht gebrochen sein

Alle Abweichungen stammen aus benannten deterministischen Zufallsströmen.

### Phase 13 – Validieren und bewerten

Harte Validierung:

- alle Geometrien liegen innerhalb erlaubter Fläche
- alle Pflichtziele sind erreichbar
- Wege verbinden mindestens einen Zugang mit dem Parknetz
- keine ungültigen Oberflächenpolygone
- keine unzulässigen Kollisionen
- Entity-/Objektbudget nicht überschritten
- verwendete Prefabs existieren

Weiche Qualitätswerte:

- Erreichbarkeit und durchschnittliche Umwegquote
- Anteil direkter Hauptwege
- Schleifenanteil
- nutzbare offene Fläche
- Regionsgrößenverteilung
- Vegetationsabdeckung
- Möblierungsabdeckung
- Symmetrieabweichung
- geschätzte Entityzahl und Bauzeit

Warnungen sind im Panel sichtbar und zeigen betroffene Geometrie im Overlay.

### Phase 14 – Vorschau

- Geometriekern läuft asynchron auf unveränderlichen Eingabekopien.
- Jede Eingabeänderung erhöht eine Revision; veraltete Resultate werden verworfen.
- Vorschau zeigt Wege, Regionen, Pflanzenpunkte, Props, Zugänge, Anker und Fehler in getrennten Layern.
- Für hohe Dichten werden Pflanzen als GPU-/Overlaypunkte dargestellt, nicht als temporäre vollständige Entities.
- UI zeigt Mengen, Flächenanteile, Seed, Rechenzeit und Warnungen.
- Zielbudget für typische Parks: erste grobe Vorschau unter 200 ms, vollständige Vorschau unter 1 s; große Parks dürfen progressiv verfeinert werden.

### Phase 15 – atomar bauen

1. unveränderliches `ParkPlan` und AssetSnapshot fixieren
2. temporären `ParkRecord` und Baujournal anlegen
3. echte Fußwegnetze erzeugen
4. Oberflächen erzeugen
5. Attraktionen und feste Props erzeugen
6. Vegetation erzeugen
7. Zaun und Tore erzeugen
8. top-level Vanilla-Entities über eigene `ParkMember`-Relationen zuordnen und
   Definition serialisieren; kein `Game.Common.Owner` an editierbare Ausgaben hängen
9. Weltzustand nach Apply validieren
10. bei Erfolg `ParkRecord` finalisieren; bei Fehler alle im Journal erfassten Entities zurückrollen

Der Bau darf nie einen halbfertigen, nicht zuordenbaren Park hinterlassen.

## Parkoptionen

### Grundprofil

- Preset: Natürlich, Stadtpark, Formal, Waldpark, Offen, Minimal
- Gesamtintensität: sehr niedrig bis sehr hoch
- Seed und „neue Variante“
- Qualitätsprofil: Performance, Ausgewogen, Dekorativ
- Symmetrie: keine, Spiegelachse, radial, leicht gebrochen
- Hauptausrichtung: automatisch, Randkante wählen, frei drehen

### Wege

- Layout: Organisch, Hybrid, Raster, Sternförmig, Konzentrisch, Voronoi/Zellen, Minimal
- Hauptwegbreite
- Nebenwegbreite
- Wegdichte
- Direktheit versus malerischer Umweg
- Kurvigkeit
- maximale Steigung
- Randweg: aus, vollständig, nur ausgewählte Seiten
- Ringanzahl
- Speichenanzahl
- Schleifenfaktor
- Sackgassen: keine, nur zu Attraktionen, erlaubt
- Kreuzungsdichte
- Haupt-/Nebenweg-Oberfläche
- echte Fußgängernavigation an/aus; im normalen Bau standardmäßig an

### Flächenaufteilung

- Wiesenanteil
- Baumhainanteil
- dichter Pflanzanteil
- Beetanteil
- Platzanteil
- minimale Regionsfläche
- Randpuffer
- organische versus geometrische Regionsgrenzen

### Vegetation

- Gesamtbepflanzung: offen bis dicht
- Baum-, Busch-, Blumen- und Bodendeckerdichte separat
- maximale Objekte pro Hektar und absolute Obergrenze
- freie Wiesenfläche
- Randverdichtung
- Clustergröße und Clusterstärke
- Artenvielfalt
- Alters-/Größenvariation, soweit das Spielasset sie unterstützt
- bevorzugte/ausgeschlossene Assets
- Zufallsmix oder fest gepinnte Assets

### Ausstattung

- Bänke an/aus und mittlerer Abstand
- Laternen an/aus, Hauptwege/Nebenwege und Abstand
- Mülleimer an/aus und Dichte
- Brunnen/Statue/Pavillon: aus, automatisch, manueller Anker
- Anzahl zentraler Attraktionen
- Assetauswahl automatisch oder konkret

### Rand und Zaun

- Zaun: aus, vollständig, ausgewählte Seiten
- Zaunasset
- Randfreiraum
- Eingangstore automatisch
- Randweg innen
- Baum-/Heckengürtel
- Straßenkante offen halten

### Gelände und Bestand

- vorhandene Objekte: respektieren, nur Vegetation überbauen, Anarchy-Modus später
- Gelände: folgen, ungeeignete Bereiche meiden
- maximale Längs- und Querneigung
- Wasser und Überflutungsflächen meiden
- Sicherheitsabstand zu Gebäuden und Netzen

### Ausgabe-Layer

Jeder Layer kann deaktiviert werden:

- Wege/Navigation
- Wegoberflächen
- Regionsoberflächen
- Bäume
- Büsche/Blumen
- Möblierung
- Beleuchtung
- Attraktionen
- Zaun

## Presets

### Natürlich

- organische Wege, mittlere Kurvigkeit
- asymmetrisch, wenige Hauptachsen
- hoher Rand- und Clusterbewuchs
- große unregelmäßige Wiesen
- wenige formale Beete
- kein Zaun als Standard

### Stadtpark

- Hybridlayout
- direkte Verbindungen zwischen Eingängen
- optionale Schleife
- mittlere Bepflanzung
- regelmäßige Bänke und Beleuchtung
- ein zentraler Attraktionsanker

### Formal

- Raster oder radial
- klare Hauptachse und Symmetrie
- gerade Wege, definierte Kreuzungen
- Beete, Baumreihen, geschnitten wirkende Randzonen
- optional vollständiger Zaun

### Waldpark

- minimaler oder organischer PathGraph
- schmale Wege
- sehr hohe Baum- und Strauchdichte
- wenige offene Lichtungen
- geringe Möblierungsdichte

### Offen

- große Wiesen
- Bäume hauptsächlich am Rand und in kleinen Gruppen
- direkte Hauptwege
- wenige Nebenwege und Props

### Minimal

- nur notwendige Verbindungen
- begrenzte Objektzahl
- geeignet für sehr kleine Flächen und schwächere Systeme

## UI-Konzept

Das Panel besitzt eine einfache Ebene und eine Expertenebene.

Vor dem Vier-Schritt-Workflow kann später ein Builder-Wähler stehen. Er tauscht
den vollständigen Satz builder-spezifischer Schritte und Einstellungen aus,
nicht nur ein Preset. Solange `PlazaBuilder` keine eigene Definition und keinen
eigenen Planer besitzt, wird ausschließlich Park angezeigt; ein funktionsloser
Plaza-Reiter ist ausdrücklich nicht vorgesehen.

Einfache Ebene:

- Presetkarten
- Dichte
- Wegstil
- Zaun ja/nein
- Attraktion ja/nein
- Seed/Variante
- Vorschau/Bauen

Expertenebene:

- aufklappbare Sektionen Wege, Flächen, Vegetation, Ausstattung, Rand, Gelände, Assets und Diagnose
- Regler zeigen Einheit und sicheren Bereich
- Abweichungen vom Preset sind markiert und einzeln zurücksetzbar
- Assetpicker mit Vorschau über Asset Icon Library
- Mengenschätzung aktualisiert sich mit der Vorschau

Weltwerkzeuge:

- Polygonpunkte/Kanten bearbeiten
- Zugang setzen
- Hauptachse zeichnen
- Anker setzen
- Ausschlussfläche zeichnen
- Randsegmente markieren
- betroffene Geometrie bei UI-Hover hervorheben

## Abhängigkeiten

### Harte Laufzeitabhängigkeiten

- Cities: Skylines II in einer unterstützten Version
- Unified Icon Library für konsistente Panelicons
- Asset Icon Library für interne Asset- und Oberflächenvorschauen

### Mitgelieferte Bibliothek, falls technisch bestätigt

- Harmony 2.2.2 für gezielte Integration in Auswahl-, Bulldoze- oder Upkeep-Verhalten, entsprechend ParkingLotTool. Vor Implementierung jedes Patches prüfen, ob ein regulärer ECS-/UI-Hook genügt. Kein Patch ohne konkreten Bedarf.

### Build-Abhängigkeiten

- offizielle CS2-Modding-Toolchain und deren `Mod.props`/`Mod.targets`
- .NET SDK passend zur Toolchain
- Node.js >= 18
- React 18, React DOM, TypeScript, webpack, Sass-Toolchain
- Spielassemblies: `Game`, relevante `Colossal.*`, `Unity.Entities`, `Unity.Collections`, `Unity.Mathematics`, `UnityEngine.CoreModule`, `Unity.InputSystem`

### Geometriebibliothek

Bevorzugt wird ein kleiner, auditierbarer C#-Kern. Für robuste Polygonoffsets und boolesche Operationen ist Clipper2 ein Kandidat. Entscheidungskriterien:

- kompatible Lizenz mit GPL-3.0
- keine native Laufzeitbibliothek
- deterministisches Verhalten
- kontrollierbare Koordinatenskalierung
- problemloser CS2-Mod-Postprocessor

Falls die aus ParkingLotTool adaptierte Geometrie alle benötigten Operationen zuverlässig abdeckt, wird keine zusätzliche Bibliothek eingeführt. NetTopologySuite ist für das MVP wegen Umfang und Integrationsrisiko nicht vorgesehen.

### Keine Laufzeitabhängigkeiten

- ParkingLotTool
- 3DScenePlatform/Python
- ArcGIS/CityEngine
- Houdini/Unreal Engine
- externe Webdienste

## Assetstrategie

- Beim Start wird ein Katalog der verfügbaren internen Assets aufgebaut und gecacht.
- Kategorien basieren auf Fähigkeiten und Maßen; bekannte Prefabnamen dienen nur als Fallback oder Testfixture.
- Presets referenzieren Kategorien und Gewichte, nicht zwingend konkrete Namen.
- Der Spieler kann Kategorieergebnisse einschränken oder einzelne Assets pinnen.
- DLC-Assets erscheinen nur, wenn verfügbar.
- Fehlt ein gespeichertes Asset, bleibt der bestehende Park unverändert. Erst beim Regenerieren wird ein Ersatz verlangt oder automatisch gewählt.
- Zufällige Assetwahl ist deterministisch und getrennt von Positionserzeugung.

## Erweiterungsarchitektur für kommende Park-DLCs

Spätere Erweiterungen sollen kein zweites Generatorsystem benötigen. Dafür werden folgende Erweiterungspunkte vorgesehen:

- `IParkFeatureProvider`: liefert neue Ankertypen und Platzierungsbedingungen
- `IRegionTypeProvider`: liefert neue semantische Regionstypen
- `IAssetClassifier`: erkennt neue DLC-Prefabs
- `IPathConstraintProvider`: ergänzt Wegeanforderungen
- `IValidationRule`: ergänzt harte oder weiche Regeln
- `IPresetProvider`: liefert neue Parkprofile

Mögliche spätere Module:

- Tierpark: Gehegepolygone, Servicewege, Besucherwege, Tore, Tier-/Habitatanker
- Vergnügungspark: Attraktionsfootprints, Warteschlangen, Hauptpromenade, Servicebereiche
- Botanischer Garten: thematische Pflanzsammlungen und Gewächshausanker
- Sportpark: Felder, Laufwege, Umkleide-/Serviceanker
- Friedhof: Rasterwege, Grabfelder, Kapellenanker
- Wasserpark: Becken, Sicherheitszonen und technische Servicewege

Version 1 speichert unbekannte Erweiterungsdaten tolerant und löscht sie nicht stillschweigend. Providerfähigkeiten werden über IDs und Versionsnummern referenziert.

## Performancebudgets

- Interaktive Polygonbearbeitung: keine synchrone Vollgenerierung pro Mausframe.
- Debounce für Konfigurationsänderungen; grobe Vorschau sofort, vollständige Vorschau asynchron.
- Typischer Park bis 40.000 m²: vollständiger Plan im Ziel unter 1 s auf Referenzhardware.
- Großer Park bis 250.000 m²: Ziel unter 5 s oder progressive Vorschau.
- Konfigurierbares hartes Objektbudget; Standardwert wird nach Messung festgelegt.
- räumliche Indizes für Kollisions- und Nachbarschaftsabfragen statt quadratischer Paarvergleiche
- Polygonoperationen in lokalen Koordinaten
- gecachte Terrain- und Hindernisabfragen pro Revision
- keine per-Frame-Aktualisierung fertiger Parks ohne Simulationsbedarf

## Fehlerbehandlung und Diagnostik

- Jede Pipelinephase liefert Ergebnis plus strukturierte Warnungen/Fehler.
- Fehler enthalten Phase, Geometriebezug, menschenlesbaren Text und optional technische Details.
- Overlay kann problematische Punkte, Kanten und Flächen hervorheben.
- Buildjournal ermöglicht vollständiges Rollback.
- Diagnoseexport enthält Definition, Planstatistik, Generatorversion, Prefabauflösung und relevante Logs, aber keine unnötigen personenbezogenen Pfade.
- Logrotation verhindert dauerhaft wachsende Logordner.

## Teststrategie

### Headless-Geometrietests

- Rechteck, Dreieck, Kreisapproximation, L-, U- und schmale Formen
- konkave Polygone und viele Randpunkte
- zufällig erzeugte gültige Polygone
- alle Layoutmodi und Presets
- deterministische Golden Outputs
- Invarianten: innerhalb Polygon, zusammenhängend, keine ungültigen Kreuzungen

### Property-/Fuzz-Tests

- zufällige Polygonbearbeitungen
- extreme, aber erlaubte Parameterkombinationen
- Seed-Reproduzierbarkeit
- keine NaN/Infinity und keine Endlosschleifen
- feste Zeitbudgets

### CS2-Paritätstests

- Triangulierung und Flächenannahme
- Netzknoten und Fußgängernavigation
- Terrainhöhen und Platzierungsbounds
- ParkRecord/ParkMember, Einzel- und Gruppenauswahl, Bulldoze und Undo
- Speichern/Laden und fehlende Prefabs

### Performancefälle

- kleine, typische und sehr große Parks
- dichteste Vegetation im erlaubten Budget
- häufige Parameteränderungen und Abbruch veralteter Tasks
- Editieren eines bestehenden Parks

## Abnahmekriterien

### MVP

- Der Nutzer kann ein gültiges Polygon zeichnen, schließen und nachbearbeiten.
- Mindestens ein Fußwegzugang kann automatisch erkannt oder manuell gesetzt werden.
- Die Park-Presets Natürlich, Stadtpark, Formal, Waldpark, Offen und Minimal liefern sichtbar unterschiedliche, reproduzierbare Ergebnisse.
- Organische, Raster-, sternförmige, konzentrische und minimale Weglayouts sind verfügbar; Hybrid ist Standard.
- Alle Pflichtzugänge und Pflichtanker sind durch ein zusammenhängendes echtes Fußwegenetz verbunden.
- Haupt- und Nebenwege können verschiedene Breiten und Oberflächen besitzen.
- Restflächen werden mindestens als Wiese, Hain, Dichtgrün, Beet, Platz oder frei klassifiziert.
- Interne Bäume, Büsche/Blumen, Bänke, Laternen und Mülleimer können regelbasiert platziert werden.
- Ein vollständiger oder segmentweiser Zaun mit Torlücken kann optional erzeugt werden, sofern ein geeigneter Vanilla-Aufbau prototypisch bestätigt ist.
- Jeder Ausgabelayer kann deaktiviert werden.
- Gleicher Seed und gleiche Eingaben erzeugen denselben Plan.
- Vorschau zeigt Geometrie und geschätzte Objektzahlen, bevor dauerhafte Entities entstehen.
- Ein gebauter Park ist als Einheit auswählbar, editierbar und vollständig löschbar.
- Fehlgeschlagener Bau hinterlässt keine dauerhaften Teilobjekte.
- Speichern und Laden erhält Park, Definition und `ParkMember`-Beziehungen.
- Fehlt ein DLC-Asset, lädt der Spielstand ohne Absturz; der Park bleibt erhalten.
- Geometrie kann headless getestet werden.

### Release 1.0, zusätzlich zum MVP

- stabile Assetauswahl und DLC-Erkennung
- Undo/Redo für Erstellen und Regenerieren
- zweisprachige Oberfläche Deutsch/Englisch
- Diagnoseexport und Logpflege
- gemessene Performancebudgets und dokumentierte Größenlimits
- Migration mindestens von der unmittelbar vorigen gespeicherten Schemaversion

## Technischer Plan und Meilensteine

### Fortschrittsabgleich – Version 0.5.0

Die Versionsnummer `0.5.0` ist eine Entwicklungsversionsnummer und bedeutet
nicht, dass Roadmap-Meilenstein M3 vollständig abgeschlossen ist. Der aktuelle
Code greift bereits in M2 und M3 vor, während einige bewusste M0-Risikospikes
noch offen sind.

Statuslegende: **erledigt** = im Spiel nachgewiesen, **teilweise** = tragfähiger
Prototyp vorhanden, aber das Abnahmekriterium ist noch nicht vollständig erfüllt,
**offen** = noch nicht implementiert oder nicht im Spiel verifiziert.

### M0 – Spike: CS2-Machbarkeit

1. **erledigt** – leeres Modprojekt, Build/Deploy, UI und Werkzeugbutton
2. **erledigt** – Polygonwerkzeug, Overlay, Bearbeitung, Undo und Snapping
3. **erledigt** – sichtbares Vanilla-Fußwegprefab identifiziert; echte,
   verbundene `NetCourse`-Testwege werden gebaut und wieder entfernt
4. **teilweise** – Wegoberflächen existieren als Kompatibilitätsfallback; seit
   0.4.0 wird zusätzlich eine gewählte Park-/Grasoberfläche für das vollständige
   Polygon gebaut, deren Ingame-Abnahme noch aussteht
5. **teilweise** – der Laufzeitkatalog liefert auswählbare Oberflächen, Bäume,
   Büsche, Bänke, Lampen und Zäune; deterministische Vorschau und Vanilla-
   Platzierung sind implementiert, aber noch nicht vollständig im Spiel abgenommen
6. **teilweise** – Wege bleiben seit 0.3.4 top-level Vanilla-Entities und sind
   über serialisierbare `ParkPathMember` einem logischen Build-Datensatz
   zugeordnet; geschlossenes Entfernen und Erkennung externer Änderungen sind
   vorhanden, Parkauswahl, vollständiges Save/Load und reguläres Bulldoze fehlen
7. **teilweise** – Wege und Ausstattung folgen per Terrain-Sampling dem Gelände;
   eine optionale Prop-Zaunkette mit Gate-Lücken ist implementiert, belastbare
   Steigungs-/Hindernisprüfung und der Ingame-Vergleich mit einem Netz fehlen

Zusätzlich bereits nachgewiesen, obwohl im ursprünglichen M0 nicht einzeln
aufgeführt: zwei getrennte Werkzeugmodi, manuelle Gates, deterministische
Seed-Varianten, orthogonale Gate-Anläufe, Qualitätsbewertung mehrerer Varianten,
Abstands-/Winkelprüfung, geglätteter typisierter PathGraph sowie eine kompakte
modusabhängige UI.

Abschluss: dokumentierte Entscheidung, welche ParkingLotTool-Komponenten übernommen werden und welche Harmony-Patches wirklich nötig sind.

M0 bleibt deshalb **offen**. Die nächsten M0-Inkremente sind verbindlich:

1. **M0.4 – implementiert, gezielte Ingame-Abnahme offen:** Park-/Grasoberfläche,
   dichte deterministische Altbäume und Büsche, wegbezogene Bänke, Lampen und
   Mülleimer
   sowie ein optionaler Zaun mit Gate-Lücken sind vorhanden. Für Möbel wird die
   tatsächliche Breite des gewählten Wegprefabs sowie die Kollisionsbounds des
   konkreten Möbelprefabs berücksichtigt. Möbel sitzen mit 20 cm Sicherheitsfuge
   direkt an der Wegkante, bleiben auf exakter Terrainhöhe und erhalten eine
   Achskorrektur passend zur Vorschau. Vegetation reserviert die vollständige
   Wegbreite und bereits geplante Möbelgrundrisse;
   Diagnosezähler für `Overridden`/`Hidden` grenzen verbleibende Fehler ein.
   Der Zaun wird in zusammenhängenden Läufen geplant und nach dem Fence-Mode-
   Prinzip aus realen Mesh-Längen lückenlos aufgebaut. Vorhandene native
   `FencePrefab`-/`NetFencePrefab`-Assets werden bevorzugt als echte
   Netz-Zaunläufe gebaut; die Prop-Kette bleibt nur Fallback. Sechs kompakte,
   voneinander unabhängige Icon-Assetwähler ersetzen die lange Parkvorlagenliste;
   sie stehen als festes 2×3-Kategorienraster neben einer Fünf-Spalten-Auswahl,
   sodass Kategorienwechsel die Panelgröße nicht mehr verändern;
   jede Kategorie kann einzeln auf automatische Parkpalettenwahl zurückgesetzt
   werden. Bäume und Büsche unterstützen eine explizite Mehrfachauswahl, aus der
   der Seed deterministisch Arten mischt. Assetauswahl, getrenntes
   Neubauen der Ausstattung und gemeinsames Entfernen sind vorhanden.
2. **M0.5 – teilweise implementiert:** Jeder neue logische Build-Datensatz
   besitzt jetzt einen eigenständig versionierten `ParkPlacementReceipt` sowie
   Puffer für Weltkoordinaten des Umrisses und der Eingänge. Weg-/Ausstattungsseed,
   Zaunstatus, Bauzustand und Elementzahl werden gespeichert; beim Laden werden
   gesperrter Planermodus, Wegenetz und Ausstattungsvorschau deterministisch
   rekonstruiert. Der erste Mehrpark-Durchstich ist ebenfalls vorhanden:
   `Park fertigstellen` trennt den aktiven Arbeitsbereich vom persistenten
   Build-Datensatz, löscht keine Vanilla-Entities und öffnet einen leeren Entwurf
   mit eigenem späteren Record. Mehrere so gebaute Parks koexistieren in der
   Stadt und ihre Anzahl wird in der UI gezeigt. Beim Abschluss erhält der Record
   einen persistenten Bündelmarker und wird nicht erneut in den Editor geladen.
   Alle erzeugten Teile bleiben eigenständige Top-level-Entities. Nur die
   Parkfläche dient als bewusster Vanilla-Bulldozer-Anker: Wird sie tatsächlich
   gelöscht (`Deleted` ohne `Temp`), markiert ein frühes Cleanup die zugehörigen
   Weg-/Zaunkanten in `Modification2` vor `ReferencesSystem`; Pflanzen, Möbel und
   Flächen folgen begrenzt in `Modification3`, danach verschwindet der Record.
   Einzelteile bleiben bis dahin top-level und damit für Move It erreichbar.
   Offen sind die Ingame-Abnahme dieses Abrisspfads, Save/Load-Abnahme,
   Migrationstests und der vollständige Transaktions-Rollback.
3. **M0.6 – Risikospikes abschließen:** Steigung und Hindernisse messen,
   Zaun als Prop-Kette oder Netz praktisch vergleichen und die Ergebnisse samt
   tatsächlich benötigter ParkingLotTool-Adaptionen/Harmony-Patches dokumentieren.

Die Ausführung erfolgt in dieser Reihenfolge:

1. **M0.4a – Sichtbarkeit abnehmen:** einen Park neu bauen und anhand der neuen
   Logzeile reale Wegbreite sowie Bank-/Lampenzahlen und deren
   `Overridden`-/`Hidden`-Status prüfen. Ziel sind sichtbare Möbel und jeweils
   null ausgeblendete Entities.
2. **M0.4b – Geometrische Freistellung:** nur falls M0.4a noch Kollisionen zeigt,
   die Prefab-Bounds der konkreten Bank/Lampe auswerten und den seitlichen
   Abstand daraus ableiten. Kein weiteres blindes Anheben und kein pauschales
   Entfernen des `Overridden`-Status, da das Spiel ihn erneut setzen kann.
3. **M0.4c – Variantenabnahme:** automatische und kopierte Parkpaletten testen;
   Bodenauflage, Ausrichtung, Wechsel der Lampenseite, lückenlose Zaunläufe,
   Gate-Lücken, Altbaum-Mischung, Einzelauswahl und vollständiges Entfernen
   bestätigen.
4. **M0.5a – implementiert, Ingame-Abnahme offen:** versionierten Bauzettel
   speichern, Umriss/Eingänge/Seeds wiederherstellen und die deterministische
   Vorschau nach Save/Load rekonstruieren.
5. **M0.5-Mehrpark-Durchstich – implementiert, Ingame-Abnahme offen:** fertigen
   Park vom Editor lösen, ohne seine Entitäten zu löschen; danach einen zweiten
   Park mit eigenem Record bauen und die Record-Anzahl anzeigen. Fertiggestellte
   Bündel werden bewusst nicht erneut in den Editor geöffnet.
6. **M0.5b – implementiert, Ingame-Abnahme offen:** die Parkfläche eines
   abgeschlossenen Records ist der Gruppen-Bulldozer-Anker. Alle übrigen
   Mitglieder bleiben einzeln bearbeitbar und löschbar. Echte Ankerlöschung wird ohne temporäre
   Bulldozer-Vorschau erkannt; Netzkanten werden in `Modification2` vor
   `ReferencesSystem`, übrige Mitglieder portionsweise in `Modification3`
   gelöscht. Danach werden überlebende Netzknoten abgekoppelt und der Record
   entfernt.
7. **M0.5c – Wegebau-Teil implementiert, Ingame-Abnahme offen:** Der Wegebau
   merkt sich vor jeder Materialisierung alle vorhandenen Entities des gewählten
   Prefabs und entdeckt nach `Apply` die tatsächlich neu entstandenen permanenten
   Kanten, Knoten und Ersatzflächen erneut. Nur die Kantenanzahl ist invariant;
   von CS2 zusammengeführte Kreuzungsknoten lösen keinen Fehlalarm mehr aus. Ein
   echter Timeout markiert alle neu entdeckten Reste zur Löschung und protokolliert
   getrennte Ist-/Sollzahlen. Ausstattungs-Rollback, Save/Load/Migration und der
   Erhalt externer Einzeländerungen bleiben offen. Erst danach M0.6 und weitere
   Module beginnen. Die sichtbare Netzgeometrie wählt nun deterministisch den
   etablierten breiten Parkweg `PedestrianPathWide01`; der schmale Namensvetter
   ist im aktuellen Vanilla-Assetsatz ein Radweg. Vor der Ausgabe werden innere
   Sackgassen eines Mehrtor-Netzes entfernt und
   nahezu kollineare Grad-2-Punkte zusammengezogen, damit redundante runde
   Node-Meshes nicht als vermeintliche Wegpunkte sichtbar bleiben.

### M1 – Headless-Domäne und Polygonkern

1. **teilweise** – `ParkPathPlan` ist typisiert; vollständige `ParkDefinition`,
   Konfiguration und Versionsschema fehlen
2. **teilweise** – Pfadplanung normalisiert Polygone, arbeitet aber noch mit
   Unity-`float2` statt eines unabhängigen lokalen Double-Kerns
3. **teilweise** – einfache Polygonprüfung und Ear-Clipping-Triangulierung sind
   vorhanden; robuste Offsets, Boolean-Operationen und vollständige Validierung fehlen
4. **offen** – Golden-, Property- und Fuzz-Harness
5. **offen** – anonymisierte Polygon-Fixtures

### M2 – Gelände, Umgebung und Eingänge

1. **teilweise** – Terrainhöhe wird für Overlay und Bau abgefragt; ein
   unveränderlicher Gelände-/Hindernis-Snapshot fehlt
2. **offen** – Geländestatistik und Steigungsprüfung
3. **offen** – automatische Randanschlusskandidaten und Bewertung
4. **erledigt** – Gates per Linksklick auf Kanten setzen und per Rechtsklick löschen
5. **offen** – stabile Gate-Reprojektion nach Polygonänderung

### M3 – PathGraph MVP

1. **teilweise** – Gates sind Pflichtknoten und ein polygonbeschränkter
   Kandidatengraph wird erzeugt; Pflichtanker fehlen
2. **erledigt** – Terminal-MST und ergänzende Verbindungen erzeugen ein
   zusammenhängendes Gate-Netz
3. **erledigt** – Haupt-, Neben- und Gate-Wege besitzen Typ und Breite
4. **teilweise** – der aktuelle Multi-Algorithmus liefert Hybridvarianten;
   eigenständige organische und minimale Strategien fehlen
5. **teilweise** – Knotenkette wird geglättet und Wege besitzen Breiten;
   robuste Korridorpolygone mit vereinigten Kreuzungen fehlen
6. **teilweise** – echte Vanilla-Fußwegnetze und gemeinsame Kreuzungsknoten
   funktionieren im Spiel; eine systematische Erreichbarkeits-/Navigationsprüfung fehlt

### M4 – zusätzliche Layoutmodi

1. Raster/formal
2. radial/sternförmig
3. konzentrisch/Ring
4. Voronoi/Zellen
5. Symmetrie und Hauptachsensteuerung
6. qualitätsbasierter Fallback auf Hybrid

### M5 – Regionen und Oberflächen

1. Restflächenbildung
2. Sliver-Bereinigung
3. semantische Klassifikation
4. Flächenbudgets und Presetregeln
5. CS2-kompatible Oberflächenzerlegung und Platzierung

### M6 – Vegetation und Ausstattung

1. Assetkatalog und Assetpicker
2. deterministische variable Poisson-Verteilung
3. Cluster und Randbänder
4. Bänke, Laternen und Mülleimer
5. zentrale Ankerobjekte
6. Zaun und Tore
7. Objektbudgets und Performanceprofile

### M7 – persistenter Park und Bearbeitung

1. ParkDefinition serialisieren
2. ParkRecord, ParkMember und PlacementReceipt ohne Owner-Verstecken editierbarer Elemente
3. atomarer Bau und Rollback
4. Auswahl und vollständiges Löschen
5. Bearbeitung mit alter Parkversion als Rückfall
6. Save/Load und Migration

### M8 – UI und Produktreife

1. einfache Presetoberfläche
2. Expertensektionen
3. Vorschau-Layer und Diagnosen
4. Lokalisierung
5. Diagnosepaket und Logpflege
6. Kompatibilitäts- und Lasttests
7. Paradox-Mods-Paketierung

## Geordnete, unabhängig prüfbare Aufgaben

1. **erledigt** – CS2-Modgerüst, Lizenz, Build und React-Panel.
2. **teilweise** – Prefab-Katalogspike; Wege/Oberflächen/Pflanzen sind belegt,
   Props und Zäune benötigen belastbare Fähigkeitsklassifikation.
3. **erledigt** – Polygonzeichnen, Bearbeitung, Snapping und Overlay.
4. **teilweise** – typisierter PathPlan vorhanden; vollständige headless
   Domainmodelle und serialisierbare Konfiguration fehlen.
5. **teilweise** – Normalisierung und Grundvalidierung vorhanden; harter
   Testkatalog fehlt.
6. **teilweise** – Terrainzugriff vorhanden; Snapshot, Hindernisse und Diagnosen fehlen.
7. **teilweise** – manuelle Zugänge vorhanden; Automatik und Reprojektion fehlen.
8. **erledigt** – minimaler zusammenhängender PathGraph erzeugt und im Spiel getestet.
9. **teilweise** – Glättung und echte Fußwegplatzierung vorhanden; robuste
   vereinigte Wegkorridore fehlen.
10. **teilweise** – Hybrid vorhanden; eigenständiger organischer Modus fehlt.
11. **offen** – Regionsermittlung und semantische Zuweisung.
12. **offen** – validierte Parkoberflächen.
13. **teilweise** – Assetkatalog und Assetpicker für sechs 0.4-Kategorien sind
   nutzbar; Klassifikation und DLC-Metadaten bleiben auszubauen.
14. **teilweise** – deterministisches abstandsbegrenztes Vegetationssampling mit
   getrennten Zufallsströmen und Budgets ist vorhanden; Felder und Cluster fehlen.
15. **teilweise** – Bänke, Laternen und Mülleimer folgen Wegseiten; reale
   Wegbreite, konkrete Prefab-Bounds, Kollisionsabstände, Höhenlage und
   Sichtbarkeitsdiagnose sind eingebaut, die Ingame-Abnahme steht aus.
   Ankerobjekte fehlen.
16. **offen** – formal, radial, konzentrisch und Zellenmodus.
17. **teilweise** – native `FencePrefab`-/`NetFencePrefab`-Netzläufe mit
   automatischen Gate-Lücken sind implementiert; die Prop-Kette bleibt als
   Fallback. Ingame-Qualität und Terrainverhalten stehen noch zur Abnahme aus.
18. **teilweise** – serialisierbarer Build-Datensatz, persistente Mitgliedschaft,
     versionierter `ParkPlacementReceipt` und Umriss-/Eingangspuffer sowie
     gestufte Übernahme für Wege, Parkfläche und Ausstattung vorhanden. Die
     die Parkfläche löst einen phasengerechten Gruppenabriss aus, während alle
     übrigen Teile einzeln bearbeitbar bleiben;
     Ingame-Save/Load- und Abrissabnahme sowie Rollback fehlen.
19. **teilweise** – letzter Testbau ist löschbar und Polygon-Undo existiert;
   persistentes Bearbeiten/Regenerieren fehlt.
20. **teilweise** – zustandsabhängiger Vier-Schritt-Workflow für Umriss, Wege,
   Ausstattung und Abschluss sowie sechs unabhängige Assetpicker für Bäume,
   Büsche, Bänke, Laternen, Zaun und Mülleimer vorhanden. Pro Schritt wird nur
   eine primäre Aktion gezeigt; die Assetkategorien stehen in einem 2×3-Raster
   neben einem stabilen Icon-Chooser. Presets und weitere Expertenoptionen fehlen.
21. **offen** – Save/Load, Migration, fehlende DLCs und Updatepfad.
22. **offen** – Performancebudgets und Standardlimits.
23. **teilweise** – die Panel-UI folgt der Spielsprache auf Deutsch oder Englisch
   (andere Sprachen fallen auf Englisch zurück); Diagnoseexport, weitere
   Sprachen und Veröffentlichungspaket fehlen.

## Community- und Produkt-Backlog

Quelle ist der erneut gelesene [Reddit-Thread „Do we need a generic park
builder?“](https://www.reddit.com/r/CitiesSkylines2/comments/1wlo81t/do_we_need_a_generic_park_builder/)
sowie die anschließenden Festlegungen für ParkManager. Die Einträge sind Ideen
und Anforderungen, keine ungeprüften Implementierungsanweisungen.

### Kurzfristig – Lebenszyklus und Glaubwürdigkeit

1. **M0.5:** mehrere Parks besitzen getrennte Bauzettel, werden beim Abschluss
   zu abgeschlossenen Records und können über ihre Parkfläche vollständig
   gebulldozt werden. Als Nächstes diesen Abriss und das Fortbestehen mehrerer
   Bündel nach Save/Load im Spiel nachweisen.
2. **M0.5:** atomarer Bau/Regenerierung mit rückstandslosem Rollback; manuelle
   Änderungen durch Move It oder Anarchy dürfen nicht still überschrieben werden.
3. **M0.6:** Steigungen, Hindernisse, Größenlimits und Objektbudgets messen und
   verständlich diagnostizieren.
4. **M0.6:** Zaun-Propkette gegen ein geeignetes Netz praktisch vergleichen;
   Gate-Lücken, Kurven, Lückenfreiheit, Bearbeitbarkeit und Performance prüfen.
5. Quelloffenheit, verwendete Algorithmen, Assetquellen, Grenzen und gemessene
   Performance für eine Veröffentlichung transparent dokumentieren.

### Parkgestaltung

1. Schnelles Füllen unregelmäßiger Restflächen als zentralen Bedienfall erhalten.
2. Stilpresets vorsehen: klassischer Gras-/Stadtpark, urban/gepflastert mit
   Pflanzkübeln, natürliches Reservat, formal sowie industriell/ungepflegt.
3. Organischere kurvige Wege und optionale „Desire Paths“ ergänzen; erzeugte
   Varianten müssen weiterhin zusammenhängend, abstands- und winkelgeprüft sein.
4. Selektive Automatisierung: einzelne Layer sperren, neu würfeln oder abschalten,
   statt bei jeder Änderung den gesamten Park zu ersetzen.
5. Regelbasierte Gruppen ergänzen, beispielsweise Bank plus Mülleimer und Lampe
   in konfigurierbaren Abständen entlang geeigneter Wegabschnitte.
6. Module/Anker für Spielplatz, Grillplatz, Café/Sitzbereich und Fahrradparken
   als spätere optionale Layer planen.
7. Langfristige Übertragbarkeit auf Naturreservate, Hobbyfarmen und allgemeine
   prozedurale Füllflächen prüfen, ohne den Park-MVP aufzublähen.

### Fahrradoption – bewusst einfacher Umfang

1. Genau eine Option **„Fahrradwege erlauben“** anbieten.
2. Bei aktiver Option denselben bereits berechneten Fußwegegraph verwenden und
   zusätzlich ein geeignetes Bike-Network auf diesen Wegverläufen anlegen; keine
   eigenen Gate-Typen und keine zweite Routengenerierung einführen.
3. In einem Spike prüfen, ob Fuß- und Fahrradnetz auf identischer Trasse stabil
   koexistieren. Nur falls das Spiel die Überlagerung ablehnt, ein kombiniertes
   Prefab oder ein unsichtbares Routingnetz als technischen Fallback untersuchen.
4. Vorschau, Bauzettel, Löschen, Save/Load und Objektbudget müssen den Schalter
   deterministisch mitführen.

### Vanilla- und DLC-Kompatibilität

1. Originalparks weiter als Asset-Paletten scannen und kohärente Baum-, Busch-,
   Möbel-, Lampen-, Zaun- und Oberflächenkombinationen kopieren; fehlende
   Kategorien nur aus kuratierten Vanilla-Fallbacks ergänzen.
2. Den Assetchooser als normale CS2-kompatible Buttons/Container belassen; keine
   native HTML-Select-Komponente verwenden, die den gemeinsamen Cohtml-Baum
   beschädigen kann.
3. Parks nach Möglichkeit als echte funktionale Parks mit Besucherlogik,
   Statistik und Nutzen untersuchen, nicht dauerhaft nur als Dekoration.
4. Kompatibilität mit dem offiziellen Park-Adventures-System als Ergänzung
   planen: dessen Parkbereiche und Module nach Möglichkeit nutzen, nicht ein
   konkurrierendes zweites Parksystem erzwingen.
5. Fehlen optionale DLC-Prefabs, muss ein Spielstand weiter laden und der Park
   mit Vanilla-Ersatz oder unveränderten vorhandenen Entities erhalten bleiben.

## Offene Fragen

Diese Punkte ändern Architektur oder sichtbares Verhalten und müssen spätestens im jeweiligen Spike entschieden werden:

1. Sollen Parks im MVP eine Vanilla-Park-/Freizeitfunktion und Besucherstatistik erhalten oder zunächst ausschließlich begehbare, dekorative Owner-Objekte sein?
2. Welcher Vanilla-Mechanismus eignet sich stabil für sichtbare Wege plus echte Fußgängernavigation: geklontes Netzprefab, unsichtbarer Fußweg unter einer Oberfläche oder Pathfinding Area?
3. Ist ein Zaun aus internen Props performant und optisch robust genug, oder existiert ein besseres internes Netzprefab?
4. Dürfen vorhandene Bäume im Polygon standardmäßig entfernt/ersetzt werden, oder blockieren sie die Generierung?
5. Wie groß dürfen Parks und Objektbudgets standardmäßig sein? Die endgültigen Werte müssen gemessen werden.
6. Werden ParkingLotTool-Komponenten direkt übernommen, als gemeinsame Bibliothek extrahiert oder sauber neu implementiert? Eine gemeinsame Bibliothek darf keine Laufzeitkopplung beider Mods erzwingen.
7. Soll Terrain-Nivellierung nach dem MVP Teil von ParkManager werden oder bewusst einem externen Werkzeug überlassen bleiben?
