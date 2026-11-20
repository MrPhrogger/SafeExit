#include <Arduino.h>
#include <WiFi.h>
#include <esp_wifi.h>
#include <esp_now.h>
#include <DHT.h>
#include <ESP32Servo.h>
#include <math.h>
#include "model.h"

// ── Pins ─────────────────────────────────────────
#define MQ5_PIN    34
#define MQ135_PIN  35
#define DHT_PIN    4
#define SERVO_PIN  13
#define LED_G      25
#define LED_Y      26
#define LED_R      27
#define BUZZER_PIN 32

#define VALVE_OPEN   0
#define VALVE_CLOSED 90

#define WARMUP_MS 300000UL   // ML enabled after this; threshold rules are always active

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

#define WINDOW_SAMPLES 10
struct Sample { uint32_t t; int mq5; int mq135; float temp; float hum; };
Sample buffer[WINDOW_SAMPLES];
uint8_t head = 0;
uint8_t count = 0;

float lastValidTemp = 25.0;
float lastValidHum = 50.0;
uint32_t lastDhtOkMs = 0;

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

// Threshold rules only. A sensor fault no longer masks a gas or fire reading.
State safetyOverride(const Features& f) {
  if (f.mq5_max > 2600) return STATE_GAS;
  if (f.mq135_max > 2500) return STATE_FIRE;
  bool leak = (f.mq5_slope > 50.0) && (f.hum_std < 2.0);
  if (leak) return STATE_GAS;
  bool steam = (f.temp_slope > 0.5) && (f.hum_slope > 0.3) && (f.mq5_slope < 50.0);
  if (steam) return STATE_COOKING;
  bool smoke = (f.mq135_slope > 35.0) && (f.mq5_slope < 50.0);
  if (smoke) return STATE_FIRE;
  return STATE_NORMAL;
}

bool sensorFault(const Sample& s) {
  if (s.mq5 <= 10 || s.mq5 >= 4085) return true;
  if (s.mq135 <= 10 || s.mq135 >= 4085) return true;
  if (millis() - lastDhtOkMs > 10000) return true;   // no valid DHT reading for 10 s
  if (s.temp < -10 || s.temp > 60) return true;
  if (s.hum < 5 || s.hum > 98) return true;
  return false;
}

void applyState(State s) {
  digitalWrite(LED_G, s == STATE_NORMAL);
  digitalWrite(LED_Y, s == STATE_COOKING || s == STATE_FAULT);
  digitalWrite(LED_R, s == STATE_GAS || s == STATE_FIRE);

  if (s == STATE_GAS || s == STATE_FIRE) valve.write(VALVE_CLOSED);
  else valve.write(VALVE_OPEN);
}

// GAS = slow pulse, FIRE = fast pulse, other states silent
void updateBuzzer(State s) {
  bool on = false;
  uint32_t now = millis();
  if (s == STATE_GAS)       on = ((now / 500) % 2) == 0;
  else if (s == STATE_FIRE) on = ((now / 150) % 2) == 0;
  digitalWrite(BUZZER_PIN, on ? HIGH : LOW);
}

void OnSent(const uint8_t *mac, esp_now_send_status_t status) {
  static int lastStatus = -1;
  if ((int)status != lastStatus) {   // print only when the status changes
    lastStatus = (int)status;
    Serial.println(status == ESP_NOW_SEND_SUCCESS ? "TX ok" : "TX FAIL (check MAC / channel)");
  }
}

void sendState(State s) {
  state_packet_t p = { 1, (uint8_t)s, 100, ++seqCounter, millis() };
  esp_now_send(exitNodeMac, (uint8_t*)&p, sizeof(p));
  lastTx = millis();
}

uint32_t lastSample = 0;
uint32_t bootTime = 0;
State prevState = STATE_NORMAL;

State pendingState = STATE_NORMAL;
int stateConfirmCount = 0;

void setup() {
  Serial.begin(115200);
  delay(2000);

  pinMode(LED_G, OUTPUT);
  pinMode(LED_Y, OUTPUT);
  pinMode(LED_R, OUTPUT);
  digitalWrite(LED_G, LOW);
  digitalWrite(LED_Y, LOW);
  digitalWrite(LED_R, LOW);

  pinMode(BUZZER_PIN, OUTPUT);
  digitalWrite(BUZZER_PIN, HIGH);   // startup beep
  delay(200);
  digitalWrite(BUZZER_PIN, LOW);

  valve.attach(SERVO_PIN, 500, 2400);
  valve.write(VALVE_OPEN);
  dht.begin();
  analogReadResolution(12);
  analogSetAttenuation(ADC_11db);

  WiFi.mode(WIFI_STA);
  esp_wifi_set_channel(1, WIFI_SECOND_CHAN_NONE);
  if (esp_now_init() != ESP_OK) Serial.println("ESP-NOW Init Failed");
  esp_now_register_send_cb(OnSent);

  esp_now_peer_info_t peer = {};
  memcpy(peer.peer_addr, exitNodeMac, 6);
  peer.channel = 1;
  peer.encrypt = false;
  esp_now_add_peer(&peer);

  Serial.print("Detection Node MAC: ");
  Serial.println(WiFi.macAddress());

  bootTime = millis();

  // ── TEST VECTOR (runs once on boot) ─────────────
  Serial.println("\n--- RUNNING TEST VECTOR ---");
  Sample testBuffer[10] = {
    {0, 1040, 990, 26.4, 48.0}, {0, 1050, 988, 26.4, 48.0}, {0, 1045, 992, 26.4, 48.0},
    {0, 1300, 995, 26.5, 48.0}, {0, 1650, 1000, 26.5, 48.1}, {0, 1980, 1005, 26.5, 48.1},
    {0, 2300, 1010, 26.5, 48.1}, {0, 2600, 1020, 26.5, 48.1}, {0, 2850, 1030, 26.6, 48.0},
    {0, 3050, 1040, 26.6, 48.0}
  };

  for (int i = 0; i < 10; i++) buffer[i] = testBuffer[i];
  count = 10;
  Features tf = computeFeatures();

  float testArr[16] = {
    tf.mq5_mean, tf.mq5_max, tf.mq5_std, tf.mq5_slope,
    tf.mq135_mean, tf.mq135_max, tf.mq135_std, tf.mq135_slope,
    tf.temp_mean, tf.temp_max, tf.temp_std, tf.temp_slope,
    tf.hum_mean, tf.hum_max, tf.hum_std, tf.hum_slope
  };

  Serial.printf("Features: %.3f, %.3f, %.3f, %.3f\n", testArr[0], testArr[1], testArr[2], testArr[3]);
  Serial.printf("Features: %.3f, %.3f, %.3f, %.3f\n", testArr[4], testArr[5], testArr[6], testArr[7]);
  Serial.printf("Features: %.3f, %.3f, %.3f, %.3f\n", testArr[8], testArr[9], testArr[10], testArr[11]);
  Serial.printf("Features: %.3f, %.3f, %.3f, %.3f\n", testArr[12], testArr[13], testArr[14], testArr[15]);

  uint8_t testPred = run_tinyml_model(testArr);
  Serial.printf("Prediction: %d (Expected: 2)\n", testPred);
  Serial.println("---------------------------\n");

  count = 0;
  head = 0;
}

void loop() {
  if (millis() - lastSample >= 500) {
    lastSample = millis();

    // DHT: read every 2 s, ignore NaN
    static uint32_t lastDhtRead = 0;
    if (millis() - lastDhtRead > 2000) {
      lastDhtRead = millis();
      float t = dht.readTemperature();
      float h = dht.readHumidity();
      if (!isnan(t) && !isnan(h)) {
        lastValidTemp = t;
        lastValidHum = h;
        lastDhtOkMs = millis();
      }
    }

    Sample s;
    s.t = millis();
    s.mq5 = analogRead(MQ5_PIN);
    s.mq135 = analogRead(MQ135_PIN);
    s.temp = lastValidTemp;
    s.hum = lastValidHum;

    buffer[head] = s;
    head = (head + 1) % WINDOW_SAMPLES;
    if (count < WINDOW_SAMPLES) count++;

    Features f = computeFeatures();
    bool fault = sensorFault(s);

    State currentState = STATE_NORMAL;

    if (count == WINDOW_SAMPLES) {
      float featureArray[16] = {
        f.mq5_mean, f.mq5_max, f.mq5_std, f.mq5_slope,
        f.mq135_mean, f.mq135_max, f.mq135_std, f.mq135_slope,
        f.temp_mean, f.temp_max, f.temp_std, f.temp_slope,
        f.hum_mean, f.hum_max, f.hum_std, f.hum_slope
      };

      // 1) Threshold rules, always active, highest priority
      State candidate = safetyOverride(f);

      // 2) Sensor fault only if no hazard was seen
      if (candidate == STATE_NORMAL && fault) candidate = STATE_FAULT;

      // 3) ML only after warm-up and when nothing above fired
      if (candidate == STATE_NORMAL && millis() - bootTime > WARMUP_MS) {
        candidate = (State)run_tinyml_model(featureArray);
      }

      // Debounce: 3 consecutive windows
      if (candidate == pendingState) {
        stateConfirmCount++;
      } else {
        pendingState = candidate;
        stateConfirmCount = 1;
      }
      currentState = (stateConfirmCount >= 3) ? pendingState : prevState;
    } else {
      currentState = fault ? STATE_FAULT : STATE_NORMAL;
    }

    if (currentState != prevState) {
      sendState(currentState);
      prevState = currentState;
      Serial.printf("STATE CHANGED -> %s\n", stateName(currentState));
    }

    applyState(prevState);

    // Debug line every 2 s
    static uint32_t lastDbg = 0;
    if (millis() - lastDbg > 2000) {
      lastDbg = millis();
      Serial.printf("MQ5=%d MQ135=%d T=%.1f H=%.1f fault=%d state=%s\n",
                    s.mq5, s.mq135, s.temp, s.hum, fault, stateName(prevState));
    }
  }

  updateBuzzer(prevState);

  // Heartbeat
  if (millis() - lastTx > 1000) sendState(prevState);

  delay(10);
}