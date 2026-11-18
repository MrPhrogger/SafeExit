#include <Arduino.h>
#include <WiFi.h>
#include <esp_wifi.h> 
#include <esp_now.h>
#include <DHT.h>
#include <ESP32Servo.h>
#include <math.h>

#define MQ5_PIN    34
#define MQ135_PIN  35
#define DHT_PIN    4
#define SERVO_PIN  13
#define LED_G      25
#define LED_Y      26
#define LED_R      27

#define VALVE_OPEN   0
#define VALVE_CLOSED 90

#define MQ5_DANGER_ABS      2600 
#define MQ135_DANGER_ABS    2500 
#define SMOKE_SLOPE_THRESH  35.0
#define LEAK_SLOPE_THRESH   50.0
#define HUM_STEAM_STD_TH    2.0
#define TEMP_STEAM_SLOPE    0.5
#define HUM_STEAM_SLOPE     0.3

enum State { STATE_NORMAL, STATE_COOKING, STATE_GAS, STATE_FIRE, STATE_FAULT };
const char* stateName(State s) {
  switch (s) {
    case STATE_NORMAL:  return "NORMAL";
    case STATE_COOKING: return "COOKING";
    case STATE_GAS:     return "GAS";
    case STATE_FIRE:    return "FIRE";
    case STATE_FAULT:   return "FAULT";
  }
  return "?";
}

DHT dht(DHT_PIN, DHT11);
Servo valve;

// Exit Node MAC
uint8_t exitNodeMac[] = {0x38, 0x18, 0x2B, 0xB1, 0x6C, 0x30}; 

typedef struct __attribute__((packed)) {
  uint8_t  node_id;
  uint8_t  state;
  uint8_t  confidence;
  uint16_t seq;
  uint32_t uptime_ms;
} state_packet_t;

uint16_t seqCounter = 0;
uint32_t lastTx = 0;

void OnDataSent(const uint8_t *mac_addr, esp_now_send_status_t status) {
  Serial.print("Send Status: ");
  Serial.println(status == ESP_NOW_SEND_SUCCESS ? "Success" : "Fail");
}

#define WINDOW_SAMPLES 10
struct Sample { uint32_t t; int mq5; int mq135; float temp; float hum; };
Sample buffer[WINDOW_SAMPLES];
uint8_t head = 0;
uint8_t count = 0;

struct Features {
  float mq5_mean, mq5_max, mq5_std, mq5_slope;
  float mq135_mean, mq135_max, mq135_std, mq135_slope;
  float temp_mean, temp_max, temp_std, temp_slope;
  float hum_mean, hum_max, hum_std, hum_slope;
};

void channelStats(const float* v, int n, float& mean, float& mx, float& sd, float& slope) {
  mean = 0; mx = v[0];
  for (int i = 0; i < n; i++) { mean += v[i]; if (v[i] > mx) mx = v[i]; }
  mean /= n;
  sd = 0;
  for (int i = 0; i < n; i++) sd += (v[i]-mean)*(v[i]-mean);
  sd = sqrtf(sd / n);
  float xm = (n-1) / 2.0f, num = 0, den = 0;
  for (int i = 0; i < n; i++) { num += (i-xm)*(v[i]-mean); den += (i-xm)*(i-xm); }
  slope = (den > 0) ? num/den : 0;
}

Features computeFeatures() {
  Features f = {};
  if (count == 0) return f;
  float mq5[WINDOW_SAMPLES], mq135[WINDOW_SAMPLES], temp[WINDOW_SAMPLES], hum[WINDOW_SAMPLES];
  for (int i = 0; i < count; i++) {
    int idx = (head - count + i + WINDOW_SAMPLES) % WINDOW_SAMPLES;
    mq5[i] = buffer[idx].mq5; mq135[i] = buffer[idx].mq135;
    temp[i] = buffer[idx].temp; hum[i] = buffer[idx].hum;
  }
  channelStats(mq5, count, f.mq5_mean, f.mq5_max, f.mq5_std, f.mq5_slope);
  channelStats(mq135, count, f.mq135_mean, f.mq135_max, f.mq135_std, f.mq135_slope);
  channelStats(temp, count, f.temp_mean, f.temp_max, f.temp_std, f.temp_slope);
  channelStats(hum, count, f.hum_mean, f.hum_max, f.hum_std, f.hum_slope);
  return f;
}

bool sensorFault(const Sample& s, const Features& f) {
  if (s.mq5 <= 10 || s.mq5 >= 4085) return true;
  if (s.mq135 <= 10 || s.mq135 >= 4085) return true;
  if (isnan(s.temp) || s.temp < -10 || s.temp > 60) return true;
  if (isnan(s.hum) || s.hum < 5 || s.hum > 98) return true;
  if (count == WINDOW_SAMPLES && f.mq5_std == 0.0 && f.mq135_std == 0.0) return true;
  return false;
}

State safetyOverride(const Features& f, bool fault) {
  if (fault) return STATE_FAULT;
  if (f.mq5_max > MQ5_DANGER_ABS) return STATE_GAS;
  if (f.mq135_max > MQ135_DANGER_ABS) return STATE_FIRE;
  bool leak = (f.mq5_slope > LEAK_SLOPE_THRESH) && (f.hum_std < HUM_STEAM_STD_TH);
  if (leak) return STATE_GAS;
  bool steam = (f.temp_slope > TEMP_STEAM_SLOPE) && (f.hum_slope > HUM_STEAM_SLOPE) && (f.mq5_slope < LEAK_SLOPE_THRESH);
  if (steam) return STATE_COOKING;
  bool smoke = (f.mq135_slope > SMOKE_SLOPE_THRESH) && (f.mq5_slope < LEAK_SLOPE_THRESH);
  if (smoke) return STATE_FIRE;
  return STATE_NORMAL;
}

uint32_t lastBlink = 0;
bool blinkState = false;

void applyState(State s) {
  if (millis() - lastBlink > 250) { blinkState = !blinkState; lastBlink = millis(); }
  digitalWrite(LED_G, LOW); digitalWrite(LED_Y, LOW); digitalWrite(LED_R, LOW);
  if (s == STATE_NORMAL || s == STATE_COOKING) digitalWrite(LED_G, HIGH);
  if (s == STATE_GAS) digitalWrite(LED_Y, blinkState ? HIGH : LOW);
  if (s == STATE_FIRE) digitalWrite(LED_R, blinkState ? HIGH : LOW);
  if (s == STATE_FAULT) digitalWrite(LED_Y, HIGH);
  if (s == STATE_GAS || s == STATE_FIRE) valve.write(VALVE_CLOSED);
  else valve.write(VALVE_OPEN);
}

void sendState(State s) {
  state_packet_t p = { 1, (uint8_t)s, 100, ++seqCounter, millis() };
  esp_now_send(exitNodeMac, (uint8_t*)&p, sizeof(p));
  lastTx = millis();
}

uint32_t lastSample = 0;
State prevState = STATE_NORMAL;

void setup() {
  Serial.begin(115200);
  delay(2000); 

  pinMode(LED_G, OUTPUT); pinMode(LED_Y, OUTPUT); pinMode(LED_R, OUTPUT);
  valve.attach(SERVO_PIN, 500, 2400);
  valve.write(VALVE_OPEN);
  dht.begin();

  analogReadResolution(12);
  analogSetAttenuation(ADC_11db);

  WiFi.mode(WIFI_STA);
  
  // 🔥 CRITICAL FIX: LOCK THE CHANNEL TO 1 🔥
  esp_wifi_set_channel(1, WIFI_SECOND_CHAN_NONE);

  if (esp_now_init() != ESP_OK) {
    Serial.println("ESP-NOW Init Failed");
    return;
  }
  esp_now_register_send_cb(OnDataSent);

  esp_now_peer_info_t peer = {};
  memcpy(peer.peer_addr, exitNodeMac, 6);
  peer.channel = 1; // Match the channel
  peer.encrypt = false;
  esp_now_add_peer(&peer);

  Serial.print("Detection Node MAC: ");
  Serial.println(WiFi.macAddress());
  Serial.println("--- SafeExit Detection Node ---");
}

void loop() {
  if (millis() - lastSample >= 500) {
    lastSample = millis();
    Sample s;
    s.t = millis(); s.mq5 = analogRead(MQ5_PIN); s.mq135 = analogRead(MQ135_PIN);
    s.temp = dht.readTemperature(); s.hum = dht.readHumidity();

    buffer[head] = s;
    head = (head + 1) % WINDOW_SAMPLES;
    if (count < WINDOW_SAMPLES) count++;

    Features f = computeFeatures();
    bool fault = sensorFault(s, f);
    State currentState = safetyOverride(f, fault);

    if (currentState != prevState) {
      sendState(currentState);
      prevState = currentState;
      Serial.printf("STATE CHANGED -> %s\n", stateName(currentState));
    }
    applyState(currentState);
  }

  if (millis() - lastTx > 1000) {
    sendState(prevState);
  }
  
  applyState(prevState); 
  delay(10);
}