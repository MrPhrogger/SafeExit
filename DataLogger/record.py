"""
Record one labeled recording from the ESP32 Data_Logger.

Usage (pip install pyserial):
  python record.py normal              # 60 s, auto-detects the port, saves data/raw/normal_01.csv
  python record.py gas --duration 40
  python record.py cooking --port COM5
  python record.py fire --duration 0   # until Ctrl+C

Labels: normal=0, cooking=1, gas=2, fire=3
Expected serial line: timestamp_ms,mq5,mq135,temp,hum
"""
import argparse, os, sys, time
import serial
from serial.tools import list_ports

LABELS = {"normal": 0, "cooking": 1, "gas": 2, "fire": 3}


def find_port():
    ports = list(list_ports.comports())
    for p in ports:
        if any(k in (p.description + str(p.manufacturer)).upper() for k in ("CP210", "CH340", "USB", "SILICON")):
            return p.device
    return ports[0].device if ports else None


def next_path(folder, name):
    os.makedirs(folder, exist_ok=True)
    i = 1
    while os.path.exists(os.path.join(folder, f"{name}_{i:02d}.csv")):
        i += 1
    return os.path.join(folder, f"{name}_{i:02d}.csv")


def parse(line):
    parts = line.strip().split(",")
    if len(parts) != 5:
        return None
    try:
        return [int(parts[0]), float(parts[1]), float(parts[2]), float(parts[3]), float(parts[4])]
    except ValueError:
        return None


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("label", choices=LABELS)
    ap.add_argument("--port")
    ap.add_argument("--baud", type=int, default=115200)
    ap.add_argument("--duration", type=float, default=60, help="seconds, 0 = until Ctrl+C")
    ap.add_argument("--folder", default="data/raw")
    a = ap.parse_args()

    port = a.port or find_port()
    if not port:
        sys.exit("No serial port found. Plug in the board or pass --port.")
    path = next_path(a.folder, a.label)
    
    try:
        ser = serial.Serial(port, a.baud, timeout=1)
    except Exception as e:
        sys.exit(f"Could not open port {port}. Error: {e}")
        
    print(f"Port {port} -> {path}  (label {a.label}={LABELS[a.label]})")
    time.sleep(2)            # opening the port can reset the ESP32
    ser.reset_input_buffer()

    n, bad, start = 0, 0, time.time()
    try:
        with open(path, "w") as f:
            f.write("timestamp_ms,mq5,mq135,temp,hum,label\n")
            while a.duration == 0 or time.time() - start < a.duration:
                raw = ser.readline().decode(errors="ignore")
                if not raw.strip():
                    continue
                row = parse(raw)
                if row is None:
                    bad += 1
                    continue
                f.write(",".join(str(x) for x in row) + f",{LABELS[a.label]}\n")
                f.flush()
                n += 1
                print(f"\r{n} samples | {time.time() - start:5.1f}s | mq5={row[1]:.0f} mq135={row[2]:.0f} "
                      f"T={row[3]:.1f} H={row[4]:.1f}", end="")
    except KeyboardInterrupt:
        pass
    finally:
        ser.close()
    print(f"\nSaved {n} samples ({bad} bad lines skipped) -> {path}")
    if n < 20:
        print("WARNING: very few samples. Check the baud rate and the logger firmware.")


if __name__ == "__main__":
    main()