#include <Arduino.h>
#include <Wire.h>
#include <LiquidCrystal_I2C.h>
#include <SPI.h>
#include <MFRC522.h>
#include <WiFi.h>
#include <esp_now.h>
#include <esp_wifi.h>
#include <WebServer.h>

#define SS_PIN     5
#define RST_PIN    17
#define BUZZER_PIN 14
#define BOOT_PIN   0
#define LCD_ADDR   0x27   // change to 0x3F if the screen stays blank

#define BUZZER_ACTIVE   true   // true = active buzzer, false = passive buzzer
#define BUZZER_ON_LEVEL HIGH   // use LOW for low-level-trigger buzzer modules

LiquidCrystal_I2C lcd(LCD_ADDR, 16, 2);
MFRC522 rfid(SS_PIN, RST_PIN);
WebServer server(80);

bool isEvacuating = false;
bool manualMode = false;                 // set by holding BOOT, ignores "normal" packets
volatile uint32_t lastHeartbeat = 0;
volatile uint8_t rxState = 0;
volatile bool rxPending = false;
uint32_t unknownUntil = 0;
uint32_t beepUntil = 0;

#define TOTAL_OCCUPANTS 3
byte registeredIDs[TOTAL_OCCUPANTS] = { 0xD1, 0x4F, 0xE4 };
bool isOut[TOTAL_OCCUPANTS] = { false, false, false };

// ── Buzzer ───────────────────────────────────────
void buzzerSet(bool on) {
#if BUZZER_ACTIVE
  digitalWrite(BUZZER_PIN, on ? BUZZER_ON_LEVEL : (BUZZER_ON_LEVEL == HIGH ? LOW : HIGH));
#else
  if (on) tone(BUZZER_PIN, 2000);
  else noTone(BUZZER_PIN);
#endif
}

void beep(uint32_t ms) {
  beepUntil = millis() + ms;
}

// Single beep on demand, pulsing alarm while evacuating
void updateBuzzer() {
  static bool last = false;
  bool on = false;
  if (millis() < beepUntil) on = true;
  else if (isEvacuating) on = ((millis() / 400) % 2) == 0;
  if (on != last) {
    buzzerSet(on);
    last = on;
  }
}

// ── Event log ────────────────────────────────────
#define MAX_EVENTS 10
String eventLog[MAX_EVENTS];
int logIndex = 0;

void addEvent(String msg) {
  char timeStr[20];
  unsigned long sec = millis() / 1000;
  snprintf(timeStr, sizeof(timeStr), "[%02lu:%02lu:%02lu]", sec / 3600, (sec / 60) % 60, sec % 60);
  eventLog[logIndex] = String(timeStr) + " " + msg;
  logIndex = (logIndex + 1) % MAX_EVENTS;
}

// ── ESP-NOW receive (keep it minimal, process in loop) ──
void OnDataRecv(const uint8_t *mac, const uint8_t *data, int len) {
  if (len < 2) return;
  rxState = data[1];
  lastHeartbeat = millis();
  rxPending = true;
}

void processRx() {
  if (!rxPending) return;
  rxPending = false;
  uint8_t s = rxState;
  Serial.printf("RX state=%u\n", s);

  if (s == 2 || s == 3) {
    if (!isEvacuating) addEvent(String("HAZARD DETECTED (") + (s == 2 ? "GAS" : "FIRE") + ")");
    isEvacuating = true;
  } else if ((s == 0 || s == 1) && !manualMode) {
    if (isEvacuating) addEvent("System Returned to Normal");
    isEvacuating = false;
    for (int i = 0; i < TOTAL_OCCUPANTS; i++) isOut[i] = false;
  }
}

// ── LCD ──────────────────────────────────────────
void lcdLine(uint8_t row, const char *text) {
  char buf[17];
  snprintf(buf, sizeof(buf), "%-16s", text);
  lcd.setCursor(0, row);
  lcd.print(buf);
}

void updateDisplay() {
  if (millis() < unknownUntil) {
    lcdLine(0, "EVACUATE");
    lcdLine(1, "UNKNOWN CARD!");
    return;
  }

  if (!isEvacuating) {
    lcdLine(0, "SafeExit Exit");
    if (lastHeartbeat == 0)                        lcdLine(1, "Waiting link...");
    else if (millis() - lastHeartbeat > 3000)      lcdLine(1, "LINK LOSS!");
    else                                           lcdLine(1, "Monitoring...");
    return;
  }

  int outCount = 0;
  for (int i = 0; i < TOTAL_OCCUPANTS; i++) if (isOut[i]) outCount++;

  char buf[17];
  snprintf(buf, sizeof(buf), "%d of %d out, %d in", outCount, TOTAL_OCCUPANTS, TOTAL_OCCUPANTS - outCount);
  lcdLine(0, "EVACUATE");
  lcdLine(1, buf);
}

// ── Dashboard ────────────────────────────────────
const char *htmlPage = R"rawliteral(
<!DOCTYPE html>
<html>
<head>
  <meta charset="UTF-8">
  <meta name="viewport" content="width=device-width, initial-scale=1.0">
  <meta http-equiv="refresh" content="2">
  <title>SafeExit Dashboard</title>
  <style>
    body { font-family: 'Segoe UI', Tahoma, Geneva, Verdana, sans-serif; background: #121212; color: #fff; margin: 0; padding: 20px; }
    .header { background: #1e1e1e; padding: 20px; border-radius: 10px; text-align: center; border-bottom: 4px solid #d32f2f; }
    .container { display: flex; flex-wrap: wrap; gap: 20px; margin-top: 20px; }
    .card { background: #1e1e1e; padding: 20px; border-radius: 10px; flex: 1; min-width: 300px; box-shadow: 0 4px 6px rgba(0,0,0,0.3); }
    h2 { color: #4caf50; margin-top: 0; }
    .log-entry { padding: 8px; background: #2c2c2c; margin-bottom: 5px; border-radius: 5px; font-family: monospace; border-left: 4px solid #ff9800; }
    .status-evac { color: #d32f2f; font-size: 2em; font-weight: bold; text-align: center; }
    .status-normal { color: #4caf50; font-size: 2em; font-weight: bold; text-align: center; }
    .roll-call { font-size: 1.5em; text-align: center; margin-top: 10px; }
  </style>
</head>
<body>
  <div class="header"><h1>SafeExit: Live Dashboard</h1></div>
  <div class="container">
    <div class="card">
      <h2>System Status</h2>
      <div>%STATUS%</div>
      <div class="roll-call">%ROLLCALL%</div>
    </div>
    <div class="card">
      <h2>Event Log</h2>
      %LOG%
    </div>
  </div>
</body>
</html>
)rawliteral";

void handleRoot() {
  String statusHTML = isEvacuating ? "<div class='status-evac'>🚨 EVACUATE</div>"
                                   : "<div class='status-normal'>Monitoring...</div>";

  int outCount = 0;
  for (int i = 0; i < TOTAL_OCCUPANTS; i++) if (isOut[i]) outCount++;
  String rollCallHTML = "Out: " + String(outCount) + " / " + String(TOTAL_OCCUPANTS);
  if (isEvacuating) rollCallHTML += " (Still Inside: " + String(TOTAL_OCCUPANTS - outCount) + ")";

  String logHTML = "";
  for (int i = 0; i < MAX_EVENTS; i++) {
    int idx = (logIndex + i) % MAX_EVENTS;
    if (eventLog[idx] != "") logHTML += "<div class='log-entry'>" + eventLog[idx] + "</div>";
  }

  String page = htmlPage;
  page.replace("%STATUS%", statusHTML);
  page.replace("%ROLLCALL%", rollCallHTML);
  page.replace("%LOG%", logHTML);
  server.send(200, "text/html", page);
}

// ── BOOT button: hold 1.5 s to toggle manual evacuation (demo backup) ──
void handleBootButton() {
  static uint32_t pressStart = 0;
  static bool handled = false;

  if (digitalRead(BOOT_PIN) == LOW) {
    if (pressStart == 0) pressStart = millis();
    if (!handled && millis() - pressStart > 1500) {
      handled = true;
      if (!manualMode) {
        manualMode = true;
        isEvacuating = true;
        addEvent("MANUAL EVACUATION");
        Serial.println("MANUAL EVACUATION ON");
      } else {
        manualMode = false;
        isEvacuating = false;
        for (int i = 0; i < TOTAL_OCCUPANTS; i++) isOut[i] = false;
        addEvent("Manual evacuation cleared");
        Serial.println("MANUAL EVACUATION OFF");
      }
    }
  } else {
    pressStart = 0;
    handled = false;
  }
}

void setup() {
  Serial.begin(115200);
  delay(1000);

  pinMode(BUZZER_PIN, OUTPUT);
  buzzerSet(false);
  buzzerSet(true);   // startup beep: if you hear nothing, it is a hardware problem
  delay(200);
  buzzerSet(false);

  pinMode(BOOT_PIN, INPUT_PULLUP);

  Wire.begin(21, 22);
  lcd.init();
  lcd.backlight();
  lcd.clear();
  lcdLine(0, "SafeExit Exit");
  lcdLine(1, "Booting...");

  SPI.begin(18, 19, 23, 5);
  rfid.PCD_Init();

  addEvent("System Booted");

  WiFi.mode(WIFI_AP_STA);
  WiFi.softAP("SafeExit_Dashboard", "password123", 1);   // channel 1
  esp_wifi_set_channel(1, WIFI_SECOND_CHAN_NONE);

  if (esp_now_init() != ESP_OK) {
    Serial.println("ESP-NOW Init Failed");
    lcdLine(1, "ESP-NOW FAIL");
    delay(2000);
  } else {
    esp_now_register_recv_cb(OnDataRecv);
  }

  server.on("/", handleRoot);
  server.begin();

  Serial.print("Exit Node MAC: ");
  Serial.println(WiFi.macAddress());
  Serial.print("Dashboard IP: ");
  Serial.println(WiFi.softAPIP());

  delay(1000);
  lcd.clear();
}

void loop() {
  server.handleClient();
  processRx();
  handleBootButton();
  updateBuzzer();

  static uint32_t lastDisplayUpdate = 0;
  if (millis() - lastDisplayUpdate > 250) {
    updateDisplay();
    lastDisplayUpdate = millis();
  }

  if (!rfid.PICC_IsNewCardPresent() || !rfid.PICC_ReadCardSerial()) return;

  byte scannedID = rfid.uid.uidByte[0];
  Serial.printf("Card scanned! UID Byte 0: 0x%02X\n", scannedID);

  if (isEvacuating) {
    bool found = false;
    for (int i = 0; i < TOTAL_OCCUPANTS; i++) {
      if (registeredIDs[i] == scannedID) {
        found = true;
        if (!isOut[i]) {
          isOut[i] = true;
          addEvent("Card Scanned: Occupant " + String(i + 1) + " OUT");
          beep(150);
        }
        break;
      }
    }
    if (!found) {
      addEvent("Unknown card scanned");
      unknownUntil = millis() + 1000;
    }
  }

  rfid.PICC_HaltA();
  updateDisplay();
}