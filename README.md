# SafeExit: On-Device Hazard Intelligence with RFID Evacuation Tracking

SafeExit is a low-cost, infrastructure-independent safety system designed for homes, schools, and small businesses. It uses Edge AI (TinyML) to distinguish between cooking steam and real gas leaks, acts automatically by closing a gas valve, and uses RFID to track evacuation roll-calls during an emergency.

## 📁 Repository Structure
This is a monorepo containing the firmware for both nodes in the system:
*   **`Detection_Node/`**: The hazard sensing node (MQ-5, MQ-135, DHT11, Servo).
*   **`Exit_Node/`**: The evacuation tracking node (RFID, LCD, Buzzer).

---

## ⚙️ How the System Works

The system operates as a complete loop: **Sensing → Intelligent Processing → Action → Accountability.**

1. **Sensing**: The Detection Node continuously samples gas (MQ-5), air quality/smoke (MQ-135), and temperature/humidity (DHT11).
2. **Intelligent Processing (Edge AI)**: Instead of simple fixed thresholds that cause false alarms, the system calculates the *slope* (rate of change) and *standard deviation* over a 5-second sliding window. This allows it to identify specific "signatures" (e.g., gas leaks cause a fast MQ-5 rise with stable humidity; steam causes a humidity/temp rise with stable gas).
3. **Action & Connectivity**: If a hazard is detected, the node closes a servo-driven gas valve and triggers local LEDs. It simultaneously transmits the state over **ESP-NOW** (a router-free, low-latency wireless protocol) to the Exit Node.
4. **Accountability (RFID)**: The Exit Node immediately displays `EVACUATE`. As occupants tap their registered RFID cards on the reader, the system counts them out. The LCD shows a live roll-call (e.g., `3 of 5 out, 2 in`), telling responders exactly who is still inside.

### 🛡️ The Safety Net
The system uses a hybrid architecture. The AI/feature-based classifier handles the complex "steam vs. gas" decisions to prevent false alarms. However, a **deterministic safety layer** runs on top of it. If a sensor disconnects, the system enters a `FAULT` state. If gas hits an absolute danger limit, the safety layer overrides the AI and forces the valve closed, guaranteeing protection even if the AI is uncertain.

---

## 🔌 Hardware Pinout & Wiring

### 1. Detection Node (ESP32-WROOM-32)
| Component | Component Pin | ESP32 Pin | Notes |
| :--- | :--- | :--- | :--- |
| **MQ-5** (Gas) | VCC / GND / AOUT | VIN / GND / **GPIO 34** | AOUT requires a voltage divider (10k + 20k) to drop 5V to 3.3V. |
| **MQ-135** (Smoke) | VCC / GND / AOUT | VIN / GND / **GPIO 35** | AOUT requires a voltage divider. |
| **DHT11** (Temp/Hum) | VCC / GND / DATA | 3V3 / GND / **GPIO 4** | Requires a 10kΩ pull-up resistor between DATA and 3V3. |
| **SG90 Servo** (Valve) | VCC / GND / PWM | VIN (5V) / GND / **GPIO 13** | Add a 470µF capacitor across 5V/GND to prevent brownouts. |
| **LED Green** | Anode (+) | **GPIO 25** | Use a 220Ω resistor to GND. |
| **LED Yellow** | Anode (+) | **GPIO 26** | Use a 220Ω resistor to GND. |
| **LED Red** | Anode (+) | **GPIO 27** | Use a 220Ω resistor to GND. |

### 2. Exit Node (ESP32-WROOM-32)
| Component | Component Pin | ESP32 Pin | Notes |
| :--- | :--- | :--- | :--- |
| **I2C LCD 1602** | VCC / GND | VIN (5V) / GND | |
| | SDA / SCL | **GPIO 21** / **GPIO 22** | Standard hardware I2C pins. |
| **RC522 RFID** | 3.3V / GND | **3V3** / GND | ⚠️ **WARNING:** Never connect VCC to 5V. |
| | RST | **GPIO 17** | |
| | SDA (SS) | **GPIO 5** | |
| | MOSI / MISO / SCK | **23** / **19** / **18** | Standard VSPI hardware bus. |
| **Buzzer** | Positive / Negative | **GPIO 14** / GND | |

---

## 🧠 Why These Sensors?

*   **MQ-5**: Specifically tuned to detect combustible gases like LPG, natural gas, and methane. It serves as the primary "gas leak" detector.
*   **MQ-135**: Tuned for air quality, smoke, and toxic gases (like CO). It serves as the primary "fire/smoke" detector.
*   **DHT11**: Serves as the "context" sensor. By combining temperature and humidity trends with the gas sensors, the system can differentiate between a dangerous gas leak (gas rises, humidity flat) and cooking steam (humidity and temp rise together). This is the key to eliminating false alarms.
*   **ESP-NOW**: Chosen because it is a peer-to-peer protocol that does not require a Wi-Fi router or internet connection. It works perfectly during power or internet outages.
*   **RC522 RFID**: A low-cost, reliable way to give occupants a physical token (card or fob) to "badge out" at the exit, providing immediate accountability to first responders.

---

## 🚀 How to Build and Test

### Prerequisites
1. Install [VS Code](https://code.visualstudio.com/) and the [PlatformIO IDE Extension](https://platformio.org/install/ide?install=vscode).
2. You will need two ESP32 boards for the full system.

### Step 1: Flash the Detection Node
1. Open the `Detection_Node` folder in VS Code (via PlatformIO).
2. Compile and upload the code.
3. Open the Serial Monitor and copy the **Detection Node MAC Address** printed on boot.

### Step 2: Flash the Exit Node
1. Open the `Exit_Node` folder in VS Code.
2. Open `src/main.cpp` and paste the Detection Node's MAC address into the `detectionNodeMac` array.
3. Register your RFID card by scanning it and replacing the `registeredIDs` array.
4. Compile and upload the code.
5. Open the Serial Monitor and copy the **Exit Node MAC Address**.

### Step 3: Link Them Together
1. Go back to the `Detection_Node` `src/main.cpp` file.
2. Paste the Exit Node's MAC address into the `exitNodeMac` array.
3. Re-upload the Detection Node code.

### Step 4: Run the Demo
1. Power both nodes. The Exit Node should display `Monitoring...`.
2. Trigger a hazard on the Detection Node (e.g., bring an unlit lighter near the MQ-5, or incense smoke near the MQ-135).
3. The Detection Node should flash its red LED and close the servo.
4. The Exit Node should instantly switch to `EVACUATE`.
5. Swipe your registered RFID card to update the roll-call count on the LCD.
6. Unplug the Detection Node to test the `LINK LOSS!` warning on the Exit Node.
