# SafeExit: On-Device Hazard Intelligence with RFID Evacuation Tracking

This repository contains the firmware for the SafeExit prototype.

## Folders
*   **Detection_Node**: Runs on the ESP32 connected to the MQ-5, MQ-135, DHT11, and Servo. It runs the Edge AI classifier and transmits alerts via ESP-NOW.
*   **Exit_Node**: Runs on the ESP32 connected to the I2C LCD and RC522 RFID reader. It receives ESP-NOW alerts and manages the evacuation roll-call.

## Setup
1. Open `Detection_Node` in VS Code with PlatformIO to flash the sensor node.
2. Open `Exit_Node` in VS Code with PlatformIO to flash the exit node.
3. Update the MAC addresses in both `main.cpp` files to match your specific ESP32s.