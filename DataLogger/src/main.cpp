#include <Arduino.h>
#include <DHT.h>

#define MQ5_PIN    34
#define MQ135_PIN  35
#define DHT_PIN    4

DHT dht(DHT_PIN, DHT11);
uint32_t lastSample = 0;

void setup() {
  Serial.begin(115200);
  delay(2000); 
  
  analogReadResolution(12);
  analogSetAttenuation(ADC_11db);
  dht.begin();
}

void loop() {
  if (millis() - lastSample < 500) return; 
  lastSample = millis();

  int mq5 = analogRead(MQ5_PIN);
  int mq135 = analogRead(MQ135_PIN);
  float temp = dht.readTemperature();
  float hum = dht.readHumidity();

  Serial.printf("%lu,%d,%d,%.1f,%.1f\n", millis(), mq5, mq135, temp, hum);
}