#include <Arduino.h>
#include <Wire.h>
#include <esp_wifi.h>
#include <LiquidCrystal_I2C.h>
#include <SPI.h>
#include <MFRC522.h>
#include <WiFi.h>
#include <esp_now.h>

#define SS_PIN     5
#define RST_PIN    17
#define BUZZER_PIN 14

LiquidCrystal_I2C lcd(0x27, 16, 2); 
MFRC522 rfid(SS_PIN, RST_PIN);

bool isEvacuating = false;
uint32_t lastHeartbeat = 0;

#define TOTAL_OCCUPANTS 3
byte registeredIDs[TOTAL_OCCUPANTS] = { 0xD1, 0xAD, 0xBE }; 
bool isOut[TOTAL_OCCUPANTS] = { false, false, false };

typedef struct __attribute__((packed)) {
  uint8_t  node_id;
  uint8_t  state;
  uint8_t  confidence;
  uint16_t seq;
  uint32_t uptime_ms;
} state_packet_t;

// Detection Node MAC
uint8_t detectionNodeMac[] = {0x1C, 0x69, 0x20, 0x96, 0xA0, 0x94}; 

void OnDataRecv(const uint8_t * mac, const uint8_t *incomingData, int len) {
  Serial.println(">>> ESP-NOW Packet Received! <<<");
  if (len < 1) return;
  
  uint8_t receivedState = incomingData[1]; // Byte 1 is the state
  Serial.printf("State received: %d\n", receivedState);
  
  lastHeartbeat = millis();
  
  if (receivedState == 2 || receivedState == 3) { // GAS or FIRE
    isEvacuating = true;
  } else if (receivedState == 0 || receivedState == 1) { // NORMAL or COOKING
    isEvacuating = false;
    for(int i = 0; i < TOTAL_OCCUPANTS; i++) isOut[i] = false; 
  }
}

void lcdLine(uint8_t row, const char* text) {
  char buf[17];
  snprintf(buf, sizeof(buf), "%-16s", text);
  lcd.setCursor(0, row);
  lcd.print(buf);
}

void updateDisplay() {
  if (!isEvacuating) {
    lcdLine(0, "SafeExit Exit");
    lcdLine(1, "Monitoring...");
    return;
  }
  if (millis() - lastHeartbeat > 3500) {
    lcdLine(0, "EVACUATE");
    lcdLine(1, "LINK LOSS!");
    return;
  }
  int outCount = 0;
  for (int i = 0; i < TOTAL_OCCUPANTS; i++) {
    if (isOut[i]) outCount++;
  }
  int inside = TOTAL_OCCUPANTS - outCount;
  lcdLine(0, "EVACUATE");
  char buf[17];
  snprintf(buf, sizeof(buf), "%d of %d out, %d in", outCount, TOTAL_OCCUPANTS, inside);
  lcdLine(1, buf);
}

void setup() {
  Serial.begin(115200);
  delay(2000); 
  
  pinMode(BUZZER_PIN, OUTPUT);
  Wire.begin(21, 22);
  lcd.init();
  lcd.backlight();
  lcdLine(0, "SafeExit Exit");
  lcdLine(1, "Booting...");

  SPI.begin(18, 19, 23, 5);
  rfid.PCD_Init();

  // Init ESP-NOW
  WiFi.mode(WIFI_STA);
  
  // 🔥 CRITICAL FIX: LOCK THE CHANNEL TO 1 🔥
  esp_wifi_set_channel(1, WIFI_SECOND_CHAN_NONE);

  if (esp_now_init() != ESP_OK) {
    Serial.println("ESP-NOW Init Failed");
    return;
  }
  esp_now_register_recv_cb(OnDataRecv);

  esp_now_peer_info_t peer = {};
  memcpy(peer.peer_addr, detectionNodeMac, 6);
  peer.channel = 1; // Match the channel
  peer.encrypt = false;
  esp_now_add_peer(&peer);

  Serial.print("Exit Node MAC: ");
  Serial.println(WiFi.macAddress());
  
  delay(1000);
  lcd.clear();
}

uint32_t lastDisplayUpdate = 0;

void loop() {
  if (millis() - lastDisplayUpdate > 250) {
    updateDisplay();
    lastDisplayUpdate = millis();
  }

  if (!rfid.PICC_IsNewCardPresent() || !rfid.PICC_ReadCardSerial()) return;

  byte scannedID = rfid.uid.uidByte[0];
  Serial.printf("\nCard scanned! UID Byte 0: 0x%02X\n", scannedID);

  if (isEvacuating) {
    bool found = false;
    for (int i = 0; i < TOTAL_OCCUPANTS; i++) {
      if (registeredIDs[i] == scannedID) {
        found = true;
        if (!isOut[i]) {
          isOut[i] = true;
          Serial.printf("-> Occupant %d is OUT\n", i+1);
          tone(BUZZER_PIN, 2000, 100);
          updateDisplay(); 
        }
        break;
      }
    }
    if (!found) {
      Serial.println("-> Unknown card!");
      lcdLine(1, "UNKNOWN CARD!");
      delay(1000);
    }
  }
  rfid.PICC_HaltA();
}