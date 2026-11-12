/* ==========================================================================
   data.js  -  SAMPLE DATA ONLY
   --------------------------------------------------------------------------
   Everything the app shows comes from this file for now.
   Later, replace buildSampleData() with a function that loads the same shape
   of data from your cloud database (e.g. Firebase) and updates it when an
   ESP32 sends a new status or alert.

   Classroom.status : 'normal' | 'emergency' | 'offline'
   Alert.status     : 'active' | 'acknowledged' | 'resolved'
   ========================================================================== */

const SAMPLE_SCHOOL = { name: 'Mabini National High School' };

const SAMPLE_USER = { name: 'Maria Reyes', role: 'Safety Coordinator' };

// Sample alert types an ESP32 device could send. Edit freely.
const ALERT_TYPES = [
  'Silent Panic Button',
  'Medical Assistance',
  'Suspicious Person',
  'Fire / Smoke Suspected'
];

function buildSampleData(now) {
  const SEC = 1000, MIN = 60 * SEC, HOUR = 60 * MIN, DAY = 24 * HOUR;

  const classrooms = [
    { id: 'a101', code: 'A-101', name: 'Grade 7 – Sampaguita', building: 'Building A', floor: '1st floor', adviser: 'Ms. R. Santos',    device: 'ESP32-A101', signal: 4, status: 'normal',    lastUpdate: now - 9 * SEC },
    { id: 'a102', code: 'A-102', name: 'Grade 7 – Dahlia',     building: 'Building A', floor: '1st floor', adviser: 'Ms. L. Cruz',      device: 'ESP32-A102', signal: 3, status: 'normal',    lastUpdate: now - 21 * SEC },
    { id: 'a201', code: 'A-201', name: 'Grade 8 – Rizal',      building: 'Building A', floor: '2nd floor', adviser: 'Mr. P. Garcia',    device: 'ESP32-A201', signal: 4, status: 'emergency', lastUpdate: now - 4 * MIN },
    { id: 'a202', code: 'A-202', name: 'Grade 8 – Bonifacio',  building: 'Building A', floor: '2nd floor', adviser: 'Ms. A. Mendoza',   device: 'ESP32-A202', signal: 2, status: 'normal',    lastUpdate: now - 33 * SEC },
    { id: 'b101', code: 'B-101', name: 'Grade 9 – Newton',     building: 'Building B', floor: '1st floor', adviser: 'Mr. D. Ramos',     device: 'ESP32-B101', signal: 0, status: 'offline',   lastUpdate: now - 3 * HOUR - 12 * MIN },
    { id: 'b102', code: 'B-102', name: 'Grade 9 – Einstein',   building: 'Building B', floor: '1st floor', adviser: 'Ms. K. Villanueva', device: 'ESP32-B102', signal: 3, status: 'normal',   lastUpdate: now - 14 * SEC },
    { id: 'b201', code: 'B-201', name: 'Grade 10 – Curie',     building: 'Building B', floor: '2nd floor', adviser: 'Ms. T. Aquino',    device: 'ESP32-B201', signal: 4, status: 'normal',    lastUpdate: now - 41 * SEC },
    { id: 'b202', code: 'B-202', name: 'Grade 10 – Galileo',   building: 'Building B', floor: '2nd floor', adviser: 'Mr. E. Bautista',  device: 'ESP32-B202', signal: 3, status: 'normal',    lastUpdate: now - 27 * SEC },
    { id: 'c101', code: 'C-101', name: 'Computer Laboratory',  building: 'Building C', floor: '1st floor', adviser: 'Mr. N. Flores',    device: 'ESP32-C101', signal: 4, status: 'normal',    lastUpdate: now - 6 * SEC },
    { id: 'c102', code: 'C-102', name: 'Science Laboratory',   building: 'Building C', floor: '1st floor', adviser: 'Ms. G. Navarro',   device: 'ESP32-C102', signal: 3, status: 'emergency', lastUpdate: now - 27 * MIN },
    { id: 'c201', code: 'C-201', name: 'Grade 11 – STEM A',    building: 'Building C', floor: '2nd floor', adviser: 'Mr. H. Castillo',  device: 'ESP32-C201', signal: 2, status: 'normal',    lastUpdate: now - 18 * SEC },
    { id: 'c202', code: 'C-202', name: 'Grade 12 – ABM',       building: 'Building C', floor: '2nd floor', adviser: 'Ms. S. Lim',       device: 'ESP32-C202', signal: 0, status: 'offline',   lastUpdate: now - DAY - 2 * HOUR }
  ];

  const alerts = [
    { id: 'al5', roomId: 'a201', type: 'Silent Panic Button',    receivedAt: now - 4 * MIN,                 status: 'active' },
    { id: 'al4', roomId: 'c102', type: 'Medical Assistance',     receivedAt: now - 27 * MIN,                status: 'acknowledged', ackAt: now - 24 * MIN },
    { id: 'al3', roomId: 'b202', type: 'Silent Panic Button',    receivedAt: now - 3 * HOUR,                status: 'resolved', ackAt: now - 3 * HOUR + MIN, resolvedAt: now - 3 * HOUR + 7 * MIN },
    { id: 'al2', roomId: 'a101', type: 'Suspicious Person',      receivedAt: now - DAY - 4 * HOUR,          status: 'resolved', ackAt: now - DAY - 4 * HOUR + 2 * MIN, resolvedAt: now - DAY - 4 * HOUR + 15 * MIN },
    { id: 'al1', roomId: 'c101', type: 'Fire / Smoke Suspected', receivedAt: now - 2 * DAY - 5 * HOUR,     status: 'resolved', ackAt: now - 2 * DAY - 5 * HOUR + MIN, resolvedAt: now - 2 * DAY - 5 * HOUR + 9 * MIN }
  ];

  const activity = [
    { ts: now - 9 * SEC,  kind: 'normal',    text: 'A-101 Grade 7 – Sampaguita reported normal conditions' },
    { ts: now - 14 * SEC, kind: 'normal',    text: 'B-102 Grade 9 – Einstein reported normal conditions' },
    { ts: now - 4 * MIN,  kind: 'emergency', text: 'A-201 Grade 8 – Rizal sent a Silent Panic Button alert' },
    { ts: now - 27 * MIN, kind: 'emergency', text: 'C-102 Science Laboratory sent a Medical Assistance alert' },
    { ts: now - 3 * HOUR - 12 * MIN, kind: 'offline', text: 'B-101 Grade 9 – Newton went offline' }
  ];

  return { classrooms, alerts, activity };
}
