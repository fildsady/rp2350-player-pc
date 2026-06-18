# SonoPilot PC — GUI v3.5 (Windows)

แอปพลิเคชัน WPF (.NET 10, Windows) สำหรับควบคุม **SonoPilot Firmware** บน Raspberry Pi Pico 2 ผ่าน USB HID  
รองรับการควบคุม Playback, EQ, Scheduler, Signal Generator, MIDI CUE และการจัดการไฟล์บน SD card

เฟิร์มแวร์ที่รองรับ: **[SonoPilot Firmware v2.0+](https://github.com/fildsady/sonopilot-firmware)**

---

## คุณสมบัติหลัก

- **ควบคุม Playback** — Play, Pause, Stop, Next, Prev, Goto (ชื่อไฟล์หรือหมายเลข track)
- **Repeat Mode** — All / One / Off / Single / Random (ใช้ RP2350 hardware TRNG)
- **10-band Graphic EQ** — ±12 dB ต่อ band, sync กับ Pico อัตโนมัติ
- **Volume & Mono** — slider 0–100, สลับ mono ได้
- **Schedule Editor** — สองโหมด (Pico Scheduler / GUI Scheduler), multi-track, กำหนดวันและเวลาหยุด
- **Pull Schedule จาก Pico** — ดึง schedule ที่บันทึกใน SD กลับมาแสดงใน GUI
- **Audio Signal Generator** — 6 waveform, ปรับความถี่ live ขณะ running
- **MIDI CUE** — เชื่อม MIDI Input ให้กดโน้ตแล้วสั่ง play/stop/goto ได้ทันที
- **Quick Controls** — ปุ่ม ▶ ⏸ ⏹ ใน connection bar ด้านบน กดได้จากทุกแท็บ
- **File Upload** — ส่งไฟล์เสียงขึ้น SD ผ่าน USB โดยตรง
- **Autoconnect** — เชื่อมต่ออัตโนมัติเมื่อเปิดแอป
- **Run on Startup** — เพิ่มเข้า Windows startup ได้
- **Dark UI** — Catppuccin dark theme สม่ำเสมอทุก element

---

## Requirements

- Windows 10/11 (64-bit)
- [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) (หรือ SDK สำหรับ build)
- SonoPilot Firmware v2.0+ เชื่อมต่อผ่าน USB
- (Optional) [loopMIDI](https://www.tobias-erichsen.de/software/loopmidi.html) — สำหรับรับ MIDI จาก DAW

---

## Build & Run

```powershell
git clone https://github.com/fildsady/sonopilot-pc
cd sonopilot-pc
dotnet build
dotnet run
```

หรือเปิด `RP2350Player.csproj` ใน Visual Studio 2022+

### Publish (self-contained exe)

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

ไฟล์ `.exe` จะอยู่ที่โฟลเดอร์ `publish/`

---

## การใช้งาน

### เชื่อมต่อ

1. เสียบ Pico 2 ผ่าน USB
2. คลิก **Connect** — GUI ค้นหา Pico ผ่าน USB HID (VID `0x2E8A` / PID `0xC0DE`) อัตโนมัติ ไม่ต้องเลือก port
3. หรือเปิด **Autoconnect** ให้ต่ออัตโนมัติทุกครั้งที่เปิดแอป

เมื่อเชื่อมต่อสำเร็จ GUI จะส่ง `status` ทันทีเพื่อดึงสถานะปัจจุบัน (repeat mode, ระดับเสียง, track ที่กำลังเล่น) มาแสดงผลโดยไม่ต้องรอ poll รอบถัดไป

---

### แท็บ Player

#### Playback

| ปุ่ม | หน้าที่ |
|------|---------|
| ⏮ PREV | เพลงก่อนหน้า |
| ▶ PLAY | เล่น / ยกเลิก pause |
| ⏸ PAUSE | หยุดชั่วคราว |
| ⏹ STOP | หยุด |
| NEXT ⏭ | เพลงถัดไป |

ในช่อง **Goto** ให้พิมพ์ชื่อไฟล์ (ไม่ต้องใส่นามสกุล) หรือ **หมายเลข track** (1-based) แล้วกด **Go** เพื่อกระโดดไปยัง track นั้นทันที

#### Repeat Mode

| ปุ่ม | โหมด |
|------|------|
| ⟳ One | วนซ้ำ track เดิม |
| ⟳ All | วนทุก track ตามลำดับ |
| ✕ Off | ไม่วน (หยุดหลัง track สุดท้าย) |
| 1 Single | เล่น track เดียวแล้วหยุด |
| 🎲 Random | สุ่ม track ถัดไป (RP2350 hardware TRNG) |

- ปุ่มที่ active จะเปลี่ยนสีทันทีที่กด โดยไม่ต้องรอ STATUS กลับมา
- เมื่อ Random mode เปิดอยู่ ปุ่ม PREV และ NEXT จะสุ่ม track ใหม่แทนการเลื่อนตามลำดับ
- การตั้งค่า repeat mode จะซิงค์ลง SD card ของ Pico อัตโนมัติ

#### Volume & Mono

- **Volume slider** — ลากหรือพิมพ์ค่า 0–100, sync กับ Pico อัตโนมัติ
- **Mono toggle** — เปิด/ปิดโหมด mono

---

### แท็บ EQ

- Slider 10 ช่อง (31.5 Hz – 16 kHz) ครอบคลุมย่าน 1-octave, range ±12 dB
- ปรับ slider → ส่งคำสั่ง `eq band N value` ไปยัง Pico ทันที
- **Reset** — คืน EQ ทุก band เป็น flat (0 dB)
- ค่า EQ ถูกบันทึกลง SD card ของ Pico ผ่านคำสั่ง `eq band`

---

### แท็บ Schedule

#### เลือกโหมด Schedule

| โหมด | การทำงาน |
|------|----------|
| 🤖 Pico Scheduler | ส่ง schedule ไปเก็บใน SD card, Pico ตรวจเวลาเองและสั่งเล่น (ทำงานแม้ปิด GUI) |
| 🖥️ GUI Scheduler | PC ตรวจเวลาและส่ง `goto` command เอง (Pico Scheduler หยุดชั่วคราว ไม่ถูกลบ) |

#### เพิ่ม Entry

คลิก **Add Entry** → ปรากฏแถวใหม่ที่มีช่องดังนี้:

| ช่อง | รายละเอียด |
|------|-----------|
| ✓ | เปิด/ปิด entry นี้ |
| Start | เวลาเริ่ม (ช่อง HH และ MM แยกกัน) |
| Stop | เวลาหยุด (ว่าง = ไม่มีเวลาหยุด) |
| Tracks | ชื่อไฟล์คั่นด้วย comma ไม่ต้องใส่นามสกุล เช่น `song1,jazz2,bgm` |
| Loops | จำนวนรอบ playlist (`0` = วนไม่รู้จบ) |
| Days | checkbox จันทร์–อาทิตย์ |

#### ส่งและดึง Schedule

| ปุ่ม | หน้าที่ |
|------|---------|
| **Send → Pico** | ส่ง schedule ทั้งหมดไปยัง Pico (`sched clear` → `sched add` ทีละ entry → `sched save`) |
| **Pull ← Pico** | ดึง schedule ที่บันทึกอยู่ใน SD กลับมาแสดงใน GUI |
| **Save File** | บันทึก schedule เป็น `.json` ลงเครื่อง PC |
| **Load File** | โหลด `.json` กลับมาแสดงใน GUI (แทนที่ schedule ปัจจุบัน) |

#### ตัวอย่าง Schedule JSON

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
`StopTime = ""` หมายถึงไม่มีเวลาหยุด | `Loops = 0` หมายถึงวนไม่รู้จบ

---

### แท็บ SigGen — Audio Signal Generator

สร้างสัญญาณเสียงทดสอบออกทาง DAC โดยตรง ใช้ทดสอบลำโพง, วัด frequency response หรือ burn-in ระบบเสียง

| Waveform | ลักษณะ |
|----------|--------|
| Sine | คลื่นไซน์บริสุทธิ์ — ทดสอบความถี่เดี่ยว |
| Square | คลื่นสี่เหลี่ยม — harmonics คี่สูง |
| Triangle | คลื่นสามเหลี่ยม — harmonics คี่ลดลงเร็ว |
| Sawtooth | คลื่นฟันเลื่อย — harmonics ทั้งคี่และคู่ |
| White Noise | สัญญาณสุ่ม broadband — ทดสอบ full range |
| Pink Noise | 1/f spectrum — ใกล้เคียงเสียงธรรมชาติ เหมาะ room acoustics |

- **Frequency slider** — ลากหรือพิมพ์ 1–20000 Hz, เปลี่ยนความถี่แบบ **live** ขณะ running โดยไม่มีช่องเงียบ
- **Volume (dBFS)** — ปรับระดับเสียง sync กับ Player tab
- กด **Start** เพื่อเริ่ม → กด **Stop** เพื่อหยุดและกลับสู่โหมดเล่นเพลงปกติ

---

### แท็บ MIDI CUE

เชื่อม MIDI Input device (USB keyboard หรือ virtual port จาก DAW ผ่าน loopMIDI) แล้วกำหนดให้โน้ตแต่ละตัวสั่งคำสั่ง playback

1. เลือก MIDI device จาก dropdown แล้วคลิก **Open**
2. คลิก **＋ Add CUE** เพื่อเพิ่มแถวใหม่
3. ระบุ MIDI note number (0–127) — GUI แสดงชื่อโน้ต (C3, D#4 ฯลฯ) อัตโนมัติ
4. เลือก Command: `goto` / `play` / `stop` / `next` / `prev`
5. ถ้าเลือก `goto` ให้ใส่ชื่อไฟล์ (ไม่ต้องใส่นามสกุล) ในช่อง Track

CUE list บันทึกอัตโนมัติเป็น `midicues.json` ในโฟลเดอร์โปรแกรม

---

## ไฟล์ที่สร้างโดยโปรแกรม

ไฟล์ต่อไปนี้ถูกบันทึกในโฟลเดอร์เดียวกับ `.exe`:

| ไฟล์ | เนื้อหา |
|------|---------|
| `settings.json` | ค่าตั้งต้น (autoconnect, repeat mode ล่าสุด) |
| `hotkeys.json` | Hotkey ที่กำหนดเอง |
| `midicues.json` | MIDI CUE list |
| `schedules.json` | Schedule ล่าสุดที่แก้ไขใน GUI |
| `lastport.txt` | *(ไม่ได้ใช้งาน — HID ค้นหาอัตโนมัติ)* |
| `windowstate.json` | ตำแหน่งและขนาดหน้าต่าง |

---

## โครงสร้างโปรเจกต์

```
sonopilot-pc/
├── MainWindow.xaml          UI layout (WPF)
├── MainWindow.xaml.cs       Code-behind (logic ทั้งหมด)
├── SerialService.cs         USB HID abstraction
├── App.xaml / App.xaml.cs   Application entry point
├── RP2350Player.csproj      .NET 10 WPF project file
└── installer.iss            Inno Setup script (installer)
```

---

## หมายเหตุ

- GUI ใช้ USB **HID** (ไม่ใช่ CDC Serial) — ไม่ต้องติดตั้ง driver เพิ่มบน Windows
- เมื่อ disconnect GUI จะ reset สถานะปุ่ม repeat ทั้งหมด เมื่อ reconnect จะดึงค่าจาก Pico ผ่าน `status` ทันที
- Repeat mode ถูกบันทึกทั้งใน `settings.json` (GUI) และ SD card ของ Pico — ทั้งสองฝั่งสอดคล้องกันเสมอ
- Random mode ใช้ RP2350 hardware TRNG บน Pico — GUI ส่ง `goto <N>` แบบสุ่มแทน `next` เพื่อให้ Pico ข้ามไป track ที่ถูกต้อง
