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

#define VALVE_OPEN   0
#define VALVE_CLOSED 90

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

float lastValidTemp = 0.0;
float lastValidHum = 0.0;

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

State safetyOverride(const Features& f, bool fault) {
  if (fault) return STATE_FAULT;
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

bool sensorFault(const Sample& s, const Features& f) {
  if (s.mq5 <= 10 || s.mq5 >= 4085) return true;
  if (s.mq135 <= 10 || s.mq135 >= 4085) return true;
  if (s.temp < -10 || s.temp > 60) return true;
  if (s.hum < 5 || s.hum > 98) return true;
  return false;
}

void applyState(State s) {
  digitalWrite(LED_G, s == STATE_NORMAL);
  digitalWrite(LED_Y, s == STATE_COOKING || s == STATE_FAULT);
  digitalWrite(LED_R, s == STATE_GAS || s == STATE_FIRE);

  if (s == STATE_GAS || s == STATE_FIRE) {
    valve.write(VALVE_CLOSED);
  } else {
    valve.write(VALVE_OPEN);
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

// ML Debounce Variables
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

  valve.attach(SERVO_PIN, 500, 2400);
  valve.write(VALVE_OPEN);
  dht.begin();
  analogReadResolution(12);
  analogSetAttenuation(ADC_11db);

  WiFi.mode(WIFI_STA);
  esp_wifi_set_channel(1, WIFI_SECOND_CHAN_NONE);
  esp_now_init();
  
  esp_now_peer_info_t peer = {};
  memcpy(peer.peer_addr, exitNodeMac, 6);
  peer.channel = 1;
  peer.encrypt = false;
  esp_now_add_peer(&peer);

  bootTime = millis();

  // ── TEST VECTOR (Runs once on boot) ─────────────
  Serial.println("\n--- RUNNING TEST VECTOR ---");
  Sample testBuffer[10] = {
    {0, 1040, 990, 26.4, 48.0}, {0, 1050, 988, 26.4, 48.0}, {0, 1045, 992, 26.4, 48.0},
    {0, 1300, 995, 26.5, 48.0}, {0, 1650, 1000, 26.5, 48.1}, {0, 1980, 1005, 26.5, 48.1},
    {0, 2300, 1010, 26.5, 48.1}, {0, 2600, 1020, 26.5, 48.1}, {0, 2850, 1030, 26.6, 48.0},
    {0, 3050, 1040, 26.6, 48.0}
  };
  
  for(int i=0; i<10; i++) { buffer[i] = testBuffer[i]; }
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
  
  // Reset for normal operation
  count = 0;
  head = 0;
}

// ── LOOP FUNCTION (This was missing!) ────────────
void loop() {
  if (millis() - lastSample >= 500) {
    lastSample = millis();
    
    // DHT Fix: Read every 2 seconds, ignore NaN
    static uint32_t lastDhtRead = 0;
    if (millis() - lastDhtRead > 2000) {
      lastDhtRead = millis();
      float t = dht.readTemperature();
      float h = dht.readHumidity();
      if (!isnan(t) && !isnan(h)) {
        lastValidTemp = t;
        lastValidHum = h;
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
    bool fault = sensorFault(s, f);
    
    State currentState = STATE_NORMAL;

    // Only run ML if warm-up (5 min) is done AND window is full
    if (millis() - bootTime > 300000 && count == WINDOW_SAMPLES) {
      float featureArray[16] = {
        f.mq5_mean, f.mq5_max, f.mq5_std, f.mq5_slope,
        f.mq135_mean, f.mq135_max, f.mq135_std, f.mq135_slope,
        f.temp_mean, f.temp_max, f.temp_std, f.temp_slope,
        f.hum_mean, f.hum_max, f.hum_std, f.hum_slope
      };
      
      State mlState = (State)run_tinyml_model(featureArray);
      State safeState = safetyOverride(f, fault);
      if (safeState != STATE_NORMAL) mlState = safeState; // Safety wins

      // Debounce logic (Require 3 consecutive windows)
      if (mlState == pendingState) {
        stateConfirmCount++;
      } else {
        pendingState = mlState;
        stateConfirmCount = 1;
      }

      if (stateConfirmCount >= 3) {
        currentState = pendingState;
      } else {
        currentState = prevState; // Keep previous state until confirmed
      }
    } else {
      // Still warming up or filling buffer
      currentState = fault ? STATE_FAULT : STATE_NORMAL; 
    }

    if (currentState != prevState) {
      sendState(currentState);
      prevState = currentState;
      Serial.printf("STATE CHANGED -> %s\n", stateName(currentState));
    }
    
    // Update the LEDs
    applyState(prevState);
  }

  // Heartbeat
  if (millis() - lastTx > 1000) {
    sendState(prevState);
  }
  
  delay(10);
}