// Writes SQL that fills a database with synthetic, non-sensitive history for performance testing.
// Usage: node seed.mjs <patients> <visits> > seed.sql
const patients = Number(process.argv[2] ?? 50000)
const visits = Number(process.argv[3] ?? 150000)
const BATCH = 2000

const out = (line) => process.stdout.write(line + '\n')

out('SET autocommit=0; SET unique_checks=0;')

for (let start = 1; start <= patients; start += BATCH) {
  const rows = []
  for (let i = start; i < start + BATCH && i <= patients; i++) {
    const file = `L-${String(i).padStart(7, '0')}`
    rows.push(`('${file}','مریض${i}','تخلص${i % 500}','پدر${i % 300}','${i % 2 ? 'Male' : 'Female'}',${1950 + (i % 60)},'07${String(i).padStart(8, '0')}','2026-01-01 00:00:00.000000',1,0)`)
  }
  out(`INSERT INTO Patients (FileNumber,FirstName,LastName,FatherName,Gender,EstimatedBirthYear,Phone,CreatedAt,CreatedByUserId,Version) VALUES ${rows.join(',')};`)
  out('COMMIT;')
}

// Patients receive ids in insert order after any existing rows, so the base id comes from the database.
out(`SET @base = (SELECT MIN(Id) FROM Patients WHERE FileNumber LIKE 'L-%');`)
out(`SET @dept = (SELECT MIN(Id) FROM Departments);`)

const DAY = 86400000
const firstDay = Date.UTC(2024, 0, 1)
for (let start = 1; start <= visits; start += BATCH) {
  const rows = []
  for (let i = start; i < start + BATCH && i <= visits; i++) {
    const date = new Date(firstDay + (i % 900) * DAY).toISOString().slice(0, 10)
    // Queue numbers start high so they can never collide with the numbers issued during the test.
    rows.push(`('L-${i}',@base+${i % patients},@dept,'Completed','${date}',${100000 + i},'${date} 08:00:00.000000',1,0)`)
  }
  out(`INSERT INTO Visits (VisitNumber,PatientId,DepartmentId,Status,VisitDate,QueueNumber,CreatedAt,CreatedByUserId,Version) VALUES ${rows.join(',')};`)
  out('COMMIT;')
}

out('SET unique_checks=1; COMMIT;')
