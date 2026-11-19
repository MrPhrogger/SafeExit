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

LiquidCrystal_I2C lcd(0x27, 16, 2); 
MFRC522 rfid(SS_PIN, RST_PIN);
WebServer server(80);

bool isEvacuating = false;
uint32_t lastHeartbeat = 0;

#define TOTAL_OCCUPANTS 3
byte registeredIDs[TOTAL_OCCUPANTS] = { 0xD1, 0xAD, 0xBE }; 
bool isOut[TOTAL_OCCUPANTS] = { false, false, false };

// ── Event Log System ─────────────────────────────
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

typedef struct __attribute__((packed)) {
  uint8_t  node_id;
  uint8_t  state;
  uint8_t  confidence;
  uint16_t seq;
  uint32_t uptime_ms;
} state_packet_t;

uint8_t detectionNodeMac[] = {0x1C, 0x69, 0x20, 0x96, 0xA0, 0x94}; 

void OnDataRecv(const uint8_t * mac, const uint8_t *incomingData, int len) {
  if (len < 1) return;
  uint8_t receivedState = incomingData[1];
  lastHeartbeat = millis();
  
  if (receivedState == 2 || receivedState == 3) { 
    if (!isEvacuating) addEvent("HAZARD DETECTED (" + String(receivedState == 2 ? "GAS" : "FIRE") + ")");
    isEvacuating = true;
  } else if (receivedState == 0 || receivedState == 1) { 
    if (isEvacuating) addEvent("System Returned to Normal");
    isEvacuating = false;
    for(int i = 0; i < TOTAL_OCCUPANTS; i++) isOut[i] = false; 
  }
}

// ── Dashboard HTML (Stored in Flash) ─────────────
const char* htmlPage = R"rawliteral(
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
  <div class="header">
    <h1>SafeExit: Live Dashboard</h1>
  </div>
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
  String statusHTML = isEvacuating ? "<div class='status-evac'>🚨 EVACUATE</div>" : "<div class='status-normal'>Monitoring...</div>";
  
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

void setup() {
  Serial.begin(115200);
  delay(2000); 
  
  pinMode(BUZZER_PIN, OUTPUT);
  Wire.begin(21, 22);
  lcd.init(); lcd.backlight();
  SPI.begin(18, 19, 23, 5);
  rfid.PCD_Init();

  addEvent("System Booted");

  // Setup Wi-Fi and ESP-NOW
  WiFi.mode(WIFI_AP_STA); // Allow both AP and Station
  WiFi.softAP("SafeExit_Dashboard", "password123"); // Create the Wi-Fi network
  esp_wifi_set_channel(1, WIFI_SECOND_CHAN_NONE);

  esp_now_init();
  esp_now_register_recv_cb(OnDataRecv);

  esp_now_peer_info_t peer = {};
  memcpy(peer.peer_addr, detectionNodeMac, 6);
  peer.channel = 1; 
  peer.encrypt = false;
  esp_now_add_peer(&peer);

  // Start the web server
  server.on("/", handleRoot);
  server.begin();

  Serial.print("Dashboard IP: ");
  Serial.println(WiFi.softAPIP()); // Print the IP to connect to
}

void loop() {
  server.handleClient();
  
  // Update LCD, RFID reading logic remains exactly the same as before
  if (!rfid.PICC_IsNewCardPresent() || !rfid.PICC_ReadCardSerial()) return;

  byte scannedID = rfid.uid.uidByte[0];
  
  if (isEvacuating) {
    for (int i = 0; i < TOTAL_OCCUPANTS; i++) {
      if (registeredIDs[i] == scannedID && !isOut[i]) {
        isOut[i] = true;
        addEvent("Card Scanned: Occupant " + String(i+1) + " OUT");
        tone(BUZZER_PIN, 2000, 100);
        break;
      }
    }
  }
  rfid.PICC_HaltA();
}