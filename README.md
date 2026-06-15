# PicoAudioCore — PC GUI (Windows)

แอปพลิเคชัน **WPF (.NET 10, Windows)** สำหรับควบคุม RP2350 MP3 Player ผ่าน USB Serial  
รองรับการตั้งค่า EQ, Schedule, Volume และเรียกดูไฟล์บน SD card

---

## คุณสมบัติ

- **เชื่อมต่อ USB Serial** — เลือก COM port, Connect/Disconnect
- **ควบคุม Playback** — Play, Pause, Stop, Next, Prev, กระโดดไปยัง track ใดก็ได้
- **32-band EQ** — ปรับ slider ±12 dB ต่อ band, sync กับ Pico อัตโนมัติ, Reset to flat
- **Volume** — slider 0–100 sync กับ Pico
- **Mono toggle**
- **Schedule Editor** — ตั้งเวลาเล่นอัตโนมัติ, หลาย track ต่อ entry, กำหนดวัน
- **Pull Schedule จาก Pico** — ดึง schedule ที่บันทึกอยู่ใน SD กลับมาแสดงใน GUI
- **Save / Load Schedule** — บันทึกเป็น JSON ลงเครื่อง PC
- **Log console** — แสดง Serial response แบบ real-time

---

## Requirements

- Windows 10/11
- [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) (หรือ SDK สำหรับ build)
- RP2350 Player firmware v1.5+ เชื่อมต่อผ่าน USB

---

## Build

```powershell
git clone <this-repo>
cd RP2350Player
dotnet build
```

Run:
```powershell
dotnet run
```

หรือเปิด `RP2350Player.csproj` ใน Visual Studio 2022+

### Publish (self-contained exe)

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

ไฟล์ exe จะอยู่ที่ `bin/Release/net10.0-windows/win-x64/publish/`

---

## การใช้งาน

### 1. เชื่อมต่อ

1. เสียบ Pico 2 ผ่าน USB
2. เลือก COM port จาก dropdown
3. คลิก **Connect**

### 2. แท็บ Player

| ปุ่ม | หน้าที่ |
|------|---------|
| Play | เล่น / ยกเลิก pause |
| Pause | หยุดชั่วคราว |
| Stop | หยุด |
| ◀ Prev | เพลงก่อนหน้า |
| Next ▶ | เพลงถัดไป |

- **Volume slider** — drag หรือพิมพ์ค่า 0–100
- **Mono toggle** — เปิด/ปิดโหมด mono

### 3. แท็บ EQ

- Slider 32 ช่อง (20 Hz – 20 kHz), range ±12 dB
- ปรับ slider → ส่งคำสั่ง `eq band N value` ไปยัง Pico ทันที
- **Reset** — คืน EQ ทุก band เป็น flat (0 dB)

### 4. แท็บ Schedule

#### เพิ่ม Entry

คลิก **Add Entry** → ปรากฏแถวใหม่ที่มีช่องดังนี้:

| ช่อง | รายละเอียด |
|------|-----------|
| ✓ | เปิด/ปิด entry นี้ |
| Start | เวลาเริ่ม (HH MM สองช่องแยก) |
| Stop | เวลาหยุด (ว่าง = ไม่มีเวลาหยุด) |
| Tracks | ชื่อไฟล์คั่นด้วย comma (ไม่ต้องใส่นามสกุล) เช่น `song1,jazz2,bgm` |
| Loops | จำนวนรอบ playlist (`0` = วนไม่รู้จบ) |
| Days | checkbox จันทร์–อาทิตย์ |

> ช่อง Start/Stop ใช้สองช่องแยก HH และ MM เพื่อป้องกันการลบ colon โดยบังเอิญ

#### ส่งไปยัง Pico

คลิก **Send → Pico** — ส่ง `sched clear` แล้วส่ง `sched add` ทีละ entry จากนั้น `sched save`

#### ดึงจาก Pico

คลิก **Pull ← Pico** — ส่ง `sched list` แล้ว parse response มาแสดงใน GUI

#### Save / Load File

- **Save File** — บันทึก schedule เป็น `.json` ลงเครื่อง PC
- **Load File** — โหลด `.json` กลับมาแสดงใน GUI (แทนที่ schedule ปัจจุบัน)

---

## Schedule JSON Format

```json
[
  {
    "Enabled": true,
    "Time": "08:00",
    "StopTime": "09:00",
    "Tracks": "morning,jazz1,jazz2",
    "Loops": 3,
    "Days": [true, true, true, true, true, false, false]
  }
]
```

`Days` = array 7 ตัว [จันทร์, อังคาร, พุธ, พฤหัส, ศุกร์, เสาร์, อาทิตย์]  
`StopTime` = `""` หมายถึงไม่มีเวลาหยุด  
`Loops` = `0` หมายถึงวนไม่รู้จบ

---

## Serial Protocol (อ้างอิง)

GUI สื่อสารกับ Pico ผ่านคำสั่ง USB Serial เหล่านี้:

```
play / pause / stop / next / prev
goto <trackname>
volume <0-100>
eq band <0-31> <value>
eq reset
mono on|off
sched list
sched clear
sched add time=HH:MM [stop=HH:MM] tracks=name1,name2 [loops=N] [days=1111111] [enabled=1]
sched save
date YYYY-MM-DD HH:MM:SS
```

---

## โครงสร้างไฟล์

```
RP2350Player/
├── MainWindow.xaml          UI layout (WPF)
├── MainWindow.xaml.cs       Code-behind (logic)
├── SerialService.cs         USB Serial abstraction
├── App.xaml / App.xaml.cs
├── RP2350Player.csproj      .NET 10 WPF project
└── installer.iss            Inno Setup script (installer)
```
