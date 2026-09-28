# Payroll Management System — Tutorial and Complete Workflow

This guide walks through the Payroll Management System from the first sign-in to a posted payroll, its payslips and its government reports. Part 1 explains the two accounts. Part 2 is the one-time setup. Part 3 is the routine you repeat every cut-off. The later parts cover special runs, year-end work, administration and troubleshooting.

> **Governing specification:** `REQUIREMENTS.md` in this folder. `plan.md` describes a separate web-based build; only its Philippine payroll rules apply here.

---

## Contents

1. [How the system is organised](#1-how-the-system-is-organised)
2. [First sign-in](#2-first-sign-in)
3. [One-time setup](#3-one-time-setup)
4. [The cut-off workflow (every pay period)](#4-the-cut-off-workflow-every-pay-period)
5. [Special runs: 13th month and final pay](#5-special-runs-13th-month-and-final-pay)
6. [Year-end and archiving](#6-year-end-and-archiving)
7. [Administration](#7-administration)
8. [Rules worth knowing](#8-rules-worth-knowing)
9. [Troubleshooting](#9-troubleshooting)
10. [Quick reference](#10-quick-reference)

---

## 1. How the system is organised

### 1.1 The two accounts

The system has exactly two roles.

| Role | What it is for | What it **cannot** do |
|---|---|---|
| **Administrator** | Owns the system and approves what gets paid. | Nothing is withheld. |
| **Accounting** | Prepares payroll: employees, detachments, timesheets, attendance, leave, runs, payslips and reports. | Approve or post a run, manage user accounts, read the audit log, back up or restore data. |

**Accounting prepares; the Administrator approves.** The system also refuses an approval from the same person who submitted the run. That separation is the company's segregation of duties, so keep the two accounts with two different people.

### 1.2 The screen layout

After sign-in you see a fixed sidebar on the left, a top bar with the page title and a live clock, and a footer showing who is signed in. The sidebar lists only the sections your role can open.

The **Dashboard** opens first. Its **Needs attention** list shows what is waiting on you, each item with a button to the screen that deals with it. Amber items stop a payroll from being paid; blue ones are reminders.

| Item | Shown to |
|---|---|
| Runs waiting for approval, approved runs not yet posted | Administrator |
| Detachments with guards but no daily rate in force today | Both |
| Draft runs in progress: not calculated, flagged for review, or ready to submit | Both |
| A cut-off that has closed with no payroll run yet (amber from three days before its pay date) | Both |
| Runs with the Administrator | Accounting |
| Leave requests waiting for a decision | Both |
| No backup since the last posted run, or a scheduled backup due | Administrator |

Above the list are headline figures: active employees, draft runs, runs awaiting approval, the last posted run, and the last backup (Administrator) or last sign-in (Accounting).

| Group | Section | Administrator | Accounting |
|---|---|:-:|:-:|
| MAIN | Dashboard | ✓ | ✓ |
| PAYROLL | Employees | ✓ | ✓ |
| | Departments & Positions | ✓ | ✓ |
| | Detachments | ✓ | ✓ |
| | Time & Attendance | ✓ | ✓ |
| | Timesheets | ✓ | ✓ |
| | Leave | ✓ | ✓ |
| | Payroll Setup | ✓ | ✓ |
| | Payroll Runs | ✓ | ✓ |
| | Approvals | ✓ | — |
| | Payslips | ✓ | ✓ |
| | Reports | ✓ | ✓ |
| ADMINISTRATION | User Accounts | ✓ | — |
| | Audit Log | ✓ | — |
| | Backup & Archive | ✓ | — |

### 1.3 The workflow at a glance

```
 ONE-TIME SETUP                                   EVERY CUT-OFF
 ──────────────                                   ─────────────
 Replace the default password                     1. Create a payroll run          (Accounting)
 Company profile & payroll rules                  2. Key timesheets / attendance   (Accounting)
 Pay calendar                                     3. Record leave, loans, standing
 Earnings, deductions, premium & statutory           deductions                    (Accounting)
 Departments, positions, work schedules           4. Lock the pay period, optional (Accounting)
 Detachments + daily rates                        5. Calculate and review          (Accounting)
 Holidays, leave types                            6. Adjust, then Submit           (Accounting)
 Employees                                        7. Approve                       (Administrator)
 User accounts                                    8. Post                          (Administrator)
                                                  9. Payslips and reports          (either)
                                                 10. Back up                       (Administrator)

 AFTER POSTING: corrections go on an Adjustment run for the same pay period (Part 5.1).
```

---

## 2. First sign-in

### 2.1 The seeded administrator

A new database comes with one account:

| Username | Password |
|---|---|
| `admin` | `Admin@123` |

> ⚠️ **The first time you sign in with this password, the system makes you replace it.** No screen opens until you do. Keep the new password somewhere safe. If the only administrator forgets it, nobody can issue a reset.

### 2.2 Signing in

1. Enter your **username or e-mail** and your **password**.
2. Click **Sign in**.

What else to expect:

- **Five wrong passwords in a row lock the account for 15 minutes.** An administrator can unlock it sooner from **User Accounts → Unlock**.
- **Forgot password?** No self-service reset exists. Ask the administrator for a temporary password.
- **A password someone else chose must be replaced.** This covers the default password, a new account's first password and a temporary password from a reset. After you sign in with one, the **Set a new password** screen opens before anything else. Enter the password you were given, then your new one twice. The only other way off that screen is **Sign out**.
- **Sessions do not time out.** You stay signed in until you click **Sign out**, or until an administrator restores a backup (which signs everyone out).
- Every sign-in, success or failure, goes to the audit trail.

### 2.3 Changing your own password

1. Click **Change password** in the top bar, next to **Sign out**.
2. Enter your current password, then the new one twice. A checklist under the new password shows which rules it meets.
3. Click **Save password**. **Cancel** takes you back to where you were.

The new password must differ from the old one and cannot be `Admin@123`.

### 2.4 Creating the Accounting account (Administrator)

1. Open **User Accounts** and click **+ New account**.
2. Fill in the **username**, **e-mail address** and **full name**, then choose the **Accounting** role.
3. Type an **initial password** or click **Generate**. A password needs at least 8 characters with an upper case letter, a lower case letter, a digit and a special character.
4. Click **Create account** and give the password to the account holder. At their first sign-in they must replace it with a password of their own.

---

## 3. One-time setup

Do these steps in order, because each one feeds the next. Accounting can do all of them. Most start with sensible seeded values, so this is mostly **review and correct**, not typing from scratch.

### Step 1 — Company profile and payroll rules

**Payroll Setup → Company & rules**

**Company profile.** This is printed on every payslip and report. Enter the registered name, trade name, address, employer TIN and RDO code, the SSS, PhilHealth and Pag-IBIG employer numbers, a contact number and e-mail, and the authorised signatory. Click **Save company profile**.

**Payroll rules.** Review each rule, then click **Save payroll rules**.

| Setting | Meaning |
|---|---|
| Pay frequency | Generates the pay calendar and picks the withholding-tax table. |
| Working-days factor | **313** = a monthly salary already covers regular holidays. **261** = weekdays only. **365** = every day. |
| Standard hours a day | 8 (Art. 83). Anything beyond is overtime. |
| Cut-off closes (days early) / Pay date (days after) | Lay out the calendar that *Generate year* creates. |
| Where a month's contributions are taken | Which run(s) in the month take SSS, PhilHealth and Pag-IBIG. |
| Settle the year's tax on the final run | Year-end tax adjustment (RR 11-2018). Leave it on. |
| Pay unworked regular holidays | Art. 94, for daily-paid staff. |
| Flag a negative net pay for review | Stops a run that would pay less than zero. |
| Accrue service incentive leave into every payslip | The legacy **5Days Inc.** column. Pays 5 ÷ 365 of a day's rate for each day rendered. **Do not** also convert SIL to cash at separation, or the same leave is paid twice. |
| Paper for printed reports | A4, dot matrix 11×14, or Epson legal. The dot matrix sizes print condensed, landscape only. |

### Step 2 — Pay calendar

**Payroll Setup → Pay calendar**

1. Choose the **year** and click **Generate year**. This creates every pay period from the payroll rules.
2. Open any period with **Edit** if you need to change it. Each period has four date fields:
   - **Period starts / ends:** the days being paid for.
   - **Cut-off from / to:** the attendance the run is allowed to read.
   - **Pay date:** when the money lands. **The pay date decides which statutory tables and which detachment rates apply.** A period paid in January uses January's tables even when it covers December.

**Period states.** Each period is **Open**, **Locked** or **Closed**.

| State | How it gets there | What it means |
|---|---|---|
| **Open** | Every new period starts here. | Attendance inside the cut-off can be entered and corrected. |
| **Locked** | Click **Lock** on the period once the cut-off's attendance is complete. | Attendance inside the cut-off is frozen for employees paid at that frequency. Until someone clicks **Reopen**, nobody can change or remove a day, approve overtime or fill in missing days. |
| **Closed** | Set automatically when a **regular** run for the period is posted. | No further **regular** run can be created for it. Adjustment, 13th-month and final-pay runs can still be created (see Part 5). |

**Reopen** on a *locked* period lets its attendance be corrected again. **Reopen** on a *closed* period has one use: paying someone the posted run left out, by letting you create a second regular run for them. It does **not** unlock the days the posted run paid, and it does not change a posted payslip.

### Step 3 — Earnings, deductions, premium and statutory tables

**Payroll Setup → Earnings & deductions**

Review the seeded pay components. The system's own components (basic pay, OT Regular/Sunday/Holiday, 5Slip, SSS and others) have a fixed code, category and method. You can change their name and tax treatment only.

To add a company-specific allowance or deduction, click **+ New** and set:

| Flag | Meaning |
|---|---|
| Taxable | Earnings only. The amount enters the withholding base. |
| Counts towards SSS and Pag-IBIG | Earnings only. Added to monthly compensation for the contribution tables. |
| Counts towards 13th month pay | Earnings only. PD 851 counts basic salary only. |
| Paid every period | Earnings only. The amount is applied automatically without being asked for. |
| Reduces taxable income | Deductions only. Taken off before tax is computed. |
| Carries an outstanding balance | Deductions only. Makes it a **loan** type, decremented until the balance is zero. |

Components are **retired, never deleted**, so a payslip already issued keeps its meaning.

**Payroll Setup → Premium rates.** This is the DOLE matrix (overtime, rest day, special and regular holiday, night differential). These are the statutory minimums. Changing a rate that is already in force creates a new row with a later effective date. It never overwrites the old one.

**Payroll Setup → Statutory tables.** SSS, PhilHealth, Pag-IBIG and withholding tax.

> ⚠️ The seeded figures were current when the software was written. **Check them against the latest SSS, PhilHealth, Pag-IBIG and BIR circulars before the first live run.** To enter a new circular, add a new schedule with a later effective date. Do not edit a table that is already in force.

### Step 4 — Departments, positions and work schedules

**Departments & Positions**

- **Departments:** the organisational units. Retire unused ones; they are never deleted.
- **Positions:** tick **Managerial or supervisory** only for Art. 82 staff. They earn **no** overtime, night differential or premium pay.
- **Work schedules:** shift start and end, unpaid break, grace period and working days. A day left unticked is a **rest day**. Attendance needs a schedule to measure lateness and overtime.

> The grace period is all-or-nothing. With 15 minutes' grace, 08:10 is on time, but 08:20 counts as **20** minutes late, not 5.

### Step 5 — Detachments and daily rates

**Detachments**

A detachment is a client post, and it is where a guard's daily rate comes from.

1. Click **+ New** and enter the **code** (e.g. `AYL-7`), **name**, optional **client** and **group code**, **region** (NCR, CAR, I, II, III, IV-A, MIMAROPA or V), **location** and notes. Click **Save detachment**.
2. Select the detachment and click **+ Post rate**. Enter the **daily rate**, the **effective from** date and, optionally, the wage order. Click **Post rate**.

Rules:

- **One code, one daily rate.** A post that pays two rates is two codes, which is how the client's sheet is written.
- **Rates are posted, never edited.** A run uses the rate in force on its **pay date**. You can enter next quarter's wage order today without changing this cut-off's pay.
- Posting a second rate on the same date **corrects** the first one.
- **Withdraw** removes a rate posted by mistake. Posted runs keep their figures. A run recalculated afterwards uses the previous rate, or shows a blocker if there is none.
- A detachment showing *"No rate posted — payroll cannot be computed for this post"* will block every run that includes its people.

### Step 6 — Holidays

**Time & Attendance → Holiday calendar**

Fixed-date holidays are seeded. Movable ones must be added each year as they are proclaimed: Holy Week, the Eid festivals and National Heroes Day. To add one, click **+ New holiday**, choose the type and click **Save holiday**:

- **Regular holiday:** 2.00× for hours worked, and paid even when not worked.
- **Special non-working day:** 1.30× for hours worked, and nothing when not worked.

### Step 7 — Leave types

**Leave → Leave types**

Review the seeded types (vacation, sick, service incentive leave and others). For each type, set the **annual credits**, **Paid**, **Accrues monthly** (for SIL) and **Convertible to cash**. An unpaid type must have zero credits.

### Step 8 — Employees

**Employees → + New employee**

| Block | Key fields |
|---|---|
| Identity | Employee number, name, sex, civil status, birth date, contact details, emergency contact. |
| Employment | Date hired, employment status, department, position, **detachment**, supervisor, **work schedule**. |
| Compensation | Pay type, basic rate, pay frequency, monthly allowance, **Minimum wage earner** (RA 9504 tax exemption). |
| Government IDs | SSS, PhilHealth, Pag-IBIG MID and TIN. Stored as digits only, so a duplicate is caught. Also the exemption switches for each agency. |
| Bank | Bank and account number, used for the disbursement file. Masked in lists. |

**Where the rate comes from:**

- An employee **assigned to a detachment** is paid the detachment's posted daily rate.
- Switch on **Pay this employee their own rate** to use the basic rate on the employee record instead. Head-office staff with no detachment always use their own rate.

**Importing many employees at once (Employees → Import…)**

Use this to load a whole roster from a spreadsheet instead of typing each record.

1. Click **Import…**. The first time, click **Save a blank template with every heading**. It saves and opens `Employee import template.csv` in `Documents\Payroll MS\Imports`. Fill it in Excel and save it as CSV or Excel Workbook (.xlsx).
2. Click **Choose file…** and pick the file. A **preview** appears. **Nothing has been written yet.**
3. Check the preview:
   - **Columns read** shows which heading went to which field. If a heading was read as the wrong field, rename it in the file and choose it again.
   - **Rows** lists each line as *New*, *Update*, *No change* or *Rejected*, with rejected rows first and the reason beside each one.
4. Click **Import N row(s)**. Rejected rows are skipped and the others are saved. Click **Save report** to keep a text file of the outcome.

Rules:

- **Only Employee No and a name are required.** Use Last Name and First Name, or a single Employee Name column written *Surname, Given*.
- **An Employee No that already exists updates that employee**, so you can correct a file and import it again. On an existing record, only the columns in the file are changed. A file with just numbers, names and contact numbers changes contact numbers and nothing else. Detachment, own-rate switch and archive state are kept.
- **Department, Position and Detach Code must already exist.** They are matched by name or code. A row naming an unknown detachment is rejected rather than imported without one, because the guard would otherwise be paid their own rate without anyone noticing.
- Dates may be day-first or month-first, but the whole file is read one way. If the file cannot tell which, a warning says day-first was assumed.
- Employment status: Probationary, Regular, Contractual, ProjectBased, PartTime, Seasonal or Consultant. Pay type: Monthly, Daily or Hourly. Minimum Wage: Yes or No.
- Government IDs are checked the same way as on the form, including the duplicate check. A duplicate found only when saving is reported as rejected in the final result.

Other actions in the employee list:

- **History** shows the salary rate history. Every rate change asks for a reason.
- **Separate** records a resignation, end of contract or retirement with an effective date. The employee is left out of runs for later periods. **Reinstate** reverses it.
- **Archive** is the system's "delete". It hides the record from lists and new runs, but keeps every payslip and report. A hard delete does not exist, because paid and filed records depend on the row. **Restore** brings an archived record back.

---

## 4. The cut-off workflow (every pay period)

```
Draft ──Calculate──► Draft (computed) ──Submit──► For approval ──Approve──► Approved ──Post──► Posted
  │                                                  │
  └──Discard──► Cancelled                            └──Return to draft──► Draft
```

### Step 1 — Create the run (Accounting)

**Payroll Runs → + New run**

1. Choose the **pay period**.
2. Choose the **kind of run**. **Regular** is the normal kind. See [Part 5](#5-special-runs-13th-month-and-final-pay) for the others.
3. Choose which employees to include (**All** or **None**, then tick individuals), add optional remarks, and click **Create run**.

The run starts as a **Draft**. You can recalculate or discard a draft as often as you need; nothing outside the run changes.

**The list of people on a run is fixed when the run is created.** You cannot add names later. If you left someone out:

- **Before posting:** discard the draft and create it again with everyone ticked. Or create a second **Regular** run for the same period with only the missing people ticked. Nobody can be on two regular runs for the same period.
- **After posting:** the period is closed. Go to **Payroll Setup → Pay calendar**, click **Reopen** on the period, then create a regular run for the missing people only.

> **One run per client, or one run for everybody?** Either works. Several regular runs can share a pay period, as long as nobody is on two of them. **But posting a regular run closes the period.** If you post client runs one at a time, create all of them before you post the first.

### Step 2 — Key the hours

Detachment staff and head-office staff are entered in different places.

#### 2a. Detachment staff: Timesheets

The client sends a printed cut-off sheet with period **totals** per person, not daily punches. Key those totals exactly as written.

1. Open **Timesheets** and choose the draft run under **PERIOD COVERED**. Only **draft regular and final-pay runs** are listed. 13th-month and adjustment runs pay no hours, so they have no timesheet.
2. The people **on this run** are banded by detachment code. If a guard is missing, they were not ticked when the run was created, so the run will not pay them. Click a person to open their card and enter:

| Column | Enter | Paid as |
|---|---|---|
| **# of Days** | Days worked | days × daily rate |
| **Regular OT** | Hours | Full OT multiplier (1.25×) |
| **Special hol. OT** | Hours | The **+0.30** increment only, because the day is already in # of Days |
| **Legal hol. OT** | Hours | The **+1.00** increment only, for the same reason |
| **Night shift** | Hours | Night differential (0.10×) |
| **Late** | Peso amount | Taken off gross |
| **Company loan** | Peso amount | Deduction. **Replaces** that period's loan amortisation. |
| **Note** | Optional, e.g. *UNIFORM*, *NO ATM* | — |

3. The card shows a running **Gross income** and **Less company loan**. This is not take-home pay yet: SSS, PhilHealth, Pag-IBIG and tax come off when the run is calculated.
4. Click **Save sheet**. **Discard changes** throws away unsaved edits, and **Clear** empties one person's figures.

A timesheet **replaces** attendance for that employee in that run. It belongs to the run, so a discarded run's figures never carry over into a new one.

> **Worked example, from the legacy screen.** Asadon, Mary Grace, detachment CS75, ₱600/day, 16–31 Aug: 13 days = 7,800.00; special holiday OT 16 h = 360.00; legal holiday OT 8 h = 600.00; night 1 h = 7.50; 5Days Inc. (computed) = 106.85; late = 27.50. Gross comes to **8,846.85** and net pay to **7,700.85**, matching the legacy system exactly.

#### 2b. Head-office staff: Time & Attendance

**Time & Attendance → Daily board.** Move between days with ◀ / ▶ / **Today**. For each person, enter the **time in and out** (and the break, if it was punched), or tick **Did not report**. The system works out hours, lateness, undertime, night differential and overtime from their work schedule.

- **Fill in the day** / **Fill in the cut-off** create the *missing* days only. Rest days and holidays are classified from the schedule and calendar, but **every other generated day is recorded as an absence until you enter punches on it**. It does not pre-fill time in and out. Use it so that nobody's days are forgotten, then enter the punches. A monthly-paid employee left with blank generated days has absences deducted, and can end up with a negative net pay.
- **Timesheet** tab: review one employee's cut-off. Overtime has to be **approved** here (*Save approval*) before it is paid.
- Only days that have already happened can be recorded.
- A day inside the cut-off of a **locked** pay period cannot be changed, and the message names the period. **Fill in the cut-off** skips those days and reports how many it skipped. A day already paid by a **posted** run is locked permanently. To correct its pay, use an Adjustment run.
- A correction needs a reason. It is stamped on the entry and written to the audit log.
- Change the **Rest day** switch when someone worked on a day other than their usual rest day.

### Step 3 — Leave, loans and standing deductions

**Leave → Requests**

- **+ File leave** records a request (dates, half day, reason). Filing does not use up any credits yet.
- **Decide** opens the request. **Approve** uses up the credits and marks the days as leave in attendance, so payroll pays them. **Reject** needs a remark. **Grant it as unpaid leave** is the only way to approve more days than the employee has credits for.
- Nobody may approve their own leave.
- **Leave → Balances → Adjust** adds or removes days, for example leave carried over from last year. Carry-over is always a manual adjustment with a reason.

**Payroll Runs → Loans & advances.** Record an SSS, Pag-IBIG or company loan: the amount borrowed, the amount taken each period and the first date it is taken. The instalment repeats until the balance reaches zero, and the last one takes whatever is left. **Balances only go down when a run is posted**, never on recalculation.

**Payroll Runs → Standing deductions.** Use this for flat amounts that end on a date rather than when a balance runs out: insurance premium, performance bond, processing fee. The full amount is taken **every** period. It is never split across the month's runs.

### Step 3a — Lock the pay period (Accounting, optional)

**Payroll Setup → Pay calendar →** **Lock** on the period.

Lock the period once every day of the cut-off has been entered and checked. After that, nobody can change the attendance the run is computed from. This matters most for head-office staff paid from Time & Attendance. Timesheets belong to the run, so locking does not affect them. To correct a day after locking: click **Reopen**, make the correction, lock the period again and recalculate.

### Step 4 — Calculate and review (Accounting)

**Payroll Runs** → select the run → **Calculate**

The run lists every employee with gross, deductions and net pay. Click **Payslip** on a row to see the itemised lines. If anything cannot be priced, such as a missing detachment rate or a missing tax table, the run lists it as a **blocker** naming the fix.

If you find a mistake, correct it at the source (timesheet, attendance, leave, loan or employee record) and click **Calculate** again.

### Step 5 — Adjustments, then Submit (Accounting)

- **Adjustment** adds a one-off earning or deduction to one employee, such as a retroactive increase or a refund. The amount is always positive; the **kind** decides whether it adds to or comes off the pay. A **remark is required** and is written to the audit trail.
- **Manual tax override (E-Withtax):** an adjustment coded `WTAX` replaces the computed withholding tax for that employee.
- Adjustments to other system-computed lines are refused, because they would be paid twice.
- When the run is right, click **Submit**. It moves to **For approval**. **Discard** cancels a draft; its reference number is never reused.

### Step 6 — Approve (Administrator)

**Approvals** lists every run waiting for approval.

1. Open the run and check totals and individual payslips.
2. Choose one:
   - **Approve** freezes the figures.
   - **Return to draft** sends it back to Accounting, with a reason that goes to the audit trail.

The person who submitted a run cannot approve it.

### Step 7 — Post (Administrator)

On the approved run in **Approvals**, click **Post**. Posting:

- locks the attendance for the period and **closes** the pay period;
- decrements loan balances;
- makes the payslips the permanent record and publishes them to **Payslips** and **Reports**.

> **Posting cannot be undone.** Check the register first.

### Step 8 — Payslips (either role)

**Payslips** → choose the **year** → pick a pay period and an employee.

- Each payslip shows every line, with **year-to-date** totals.
- **Export PDF** saves one payslip, and **Export whole run** saves one PDF for the whole run. **Open** shows the file.
- Only **posted** runs appear here.
- PDFs print amounts without the ₱ sign, because the built-in PDF fonts do not have it. The header states "All amounts in Philippine pesos (PHP)" instead.

### Step 9 — Reports (either role)

**Reports** → choose a report and its run or date range → **Export CSV** or **Export PDF** → **Open**.

| Report | Use |
|---|---|
| **Payroll register** | Every employee and every pay line for a run. It **checks its own totals** against the run and says "do not file or pay from this report" if anything disagrees. |
| **SSS contribution report (R-3 / R-5)** | Monthly SSS remittance. |
| **PhilHealth remittance report (RF-1)** | Monthly PhilHealth remittance. |
| **Pag-IBIG remittance report (MCRF)** | Monthly Pag-IBIG remittance. |
| **Withholding tax (BIR 1601-C)** | Monthly tax remittance. |
| **Bank disbursement file** | A generic layout (five fields) that you copy into your bank's upload template. |
| **Payroll summary by detachment** | The client's layout: one line per employee, subtotals per detachment, and the detachment code and name as the last two columns. |
| **Attendance and absences** | Attendance summary for a period. |
| **Annual alphalist** | Year-end BIR alphalist. |

Things to know when reading reports:

- A **remittance month is the pay period's month**, not the pay date's. A cut-off ending 30 September and paid on 5 October counts as September.
- The **payroll summary takes whole cut-offs only**. A run that your date range only partly covers is left out and named in a warning; it is never counted whole or prorated.
- On the payroll summary, **Late sits in the earnings block and comes off gross**, the way the client's sheet does it. So its Gross and Total Deductions differ from the payslip's by exactly the late amount, while Net is the same.
- CSV files open directly in Excel, accented names included.

### Step 10 — Back up (Administrator)

**Backup & Archive → Back up now** after every posted run, even if automatic backups are on.

---

## 5. Special runs: 13th month and final pay

Create these the same way as a regular run. Pick the kind under **KIND OF RUN** in the **+ New run** window.

| Kind | What it does |
|---|---|
| **13th month** | Pays the PD 851 entitlement (basic salary ÷ 12). Up to ₱90,000 is tax-exempt and the excess is taxed. **No** contributions or loan instalments are taken. |
| **Final pay** | For a separated employee. Adds unused convertible leave paid out as cash and a prorated 13th month, and settles the year's tax. |
| **Adjustment** | Corrects a cut-off that has already been paid and posted. It pays **only the one-off adjustments entered on it**. See 5.1. |

**All three kinds can be created on a closed pay period.** Only a second *regular* run is refused. So, for example, you can run the 13th month on December's second cut-off after that cut-off's regular payroll has been posted.

The approve → post sequence is the same as for a regular run.

### 5.1 Correcting a posted payroll with an Adjustment run

Use this when a posted payslip was wrong: an underpaid day, a missed allowance or a retroactive increase.

1. **Payroll Runs → + New run.** Choose **Adjustment** under **KIND OF RUN**. The period list now includes closed periods, marked *· closed*. Pick the period being corrected and tick **only the people being corrected**.
2. Select the run. For each person, click **Adjustment** and enter the kind (earning or deduction), the amount, the **Taxable** switch and a remark saying what is being corrected.
3. Click **Calculate**. Each payslip holds the adjustments and nothing else. There is no basic pay, 5Days Inc., SSS, PhilHealth, Pag-IBIG, loan instalment or standing deduction, because the regular run already paid or took all of those. Taking them again would pay or deduct them twice.
4. Submit, approve and post as usual.

Things to know:

- **Tax.** Withholding comes from the period table applied to the adjustment amount alone, which for a small amount is usually zero. The year's correct tax is settled on the last regular run of the year, which includes this payslip. To withhold a specific amount now, add a `WTAX` deduction adjustment.
- **Everyone on the run needs an adjustment.** Anyone without one is flagged, and the run cannot be submitted. Enter their adjustment, or discard the run and create it again without them.
- **Each person can be on only one adjustment run per period.** To correct the same person twice, put both adjustments on that one run.
- **To recover an overpayment**, put a *deduction* adjustment on the person's **next regular run**. On an adjustment run, a deduction by itself gives a negative net pay, which is flagged and stops the run from being submitted.

---

## 6. Year-end and archiving

1. **Year-end tax.** With *Settle the year's tax on the final run* switched on, the last regular run of the year trues up each employee's withholding against the annual table.
2. **Annual alphalist.** Produce it from **Reports**.
3. **Next year's calendar.** **Payroll Setup → Pay calendar → Generate year** for the new year, then add the new year's movable holidays.
4. **Leave carry-over.** Enter any carried-over leave through **Leave → Balances → Adjust**.
5. **Archive a closed year** (Administrator). In **Backup & Archive → Payroll years**, **Archive** saves a closed year to the Archive folder. **Remove** takes it off the live database, and is only offered once the year has no open runs and is past the retention period set under *Keep years online*.

---

## 7. Administration

### 7.1 User accounts

In **User Accounts**:

- **Edit** changes a user's name, e-mail or role, or switches off **Account is active**. Deactivating blocks sign-in but keeps the account's history.
- **Reset** issues a **temporary password**. It is shown **once only** (only its hash is stored), so hand it over straight away. The holder must replace it the next time they sign in.
- **Unlock** clears a lockout before the 15 minutes are up.

### 7.2 Audit log

**Audit Log** records every sign-in, failed attempt, lockout, account change, correction, adjustment, approval, posting and refused action. You can search it by action, actor or detail.

### 7.3 Backup and restore

In **Backup & Archive**:

| Control | Meaning |
|---|---|
| **Back up automatically when due**, **Every (days)**, **Keep** | Automatic backup schedule and how many copies to keep. Manual backups are never removed by the keep count. |
| **Back up now** | Immediate backup. |
| **Restore** | Replaces the **entire** database with the chosen backup. Everyone is signed out and must sign in again against the restored data. |
| **Delete** | Removes a backup file. |

Backups are saved in your **Documents** folder, not next to the live database, so uninstalling the app does not remove them. **Copy them off the machine regularly.**

---

## 8. Rules worth knowing

- **Nothing that has been used is ever edited or deleted. It is replaced from a later date instead.** Statutory tables, premium rates and detachment rates all work this way, so any old payslip can still be explained.
- **The pay date decides the rates.** Tax tables, contribution schedules, premium rates and detachment rates are all read as of the run's pay date.
- **Salary coverage.** A premium pays only the part the salary does not already cover. A monthly-paid employee (factor 313) working a regular holiday gets the extra 1.00×, not 2.00×. A daily-paid employee gets the full multiplier. Overtime is never covered.
- **Lateness and absence deductions apply to monthly-paid staff only.** A daily-paid employee's short hours are already reflected in what they are paid.
- **Minimum wage earners** are tax-exempt on basic, holiday, overtime and night differential pay.
- **Rounding:** half away from zero, to 2 decimal places on each payslip line.
- **The BIR has no bi-weekly withholding table**, so bi-weekly pay frequency shows no tax table. Use weekly, semi-monthly or monthly.
- **Payslips are snapshots.** Renaming a detachment or changing an employee record later does not change a payslip or report already produced.

---

## 9. Troubleshooting

| Symptom | Cause and fix |
|---|---|
| "Account locked" at sign-in | Five failed attempts. Wait 15 minutes, or an Administrator uses **User Accounts → Unlock**. |
| A sidebar section is missing | Your role does not include it (see 1.2). |
| Timesheets says "No draft payroll run is open" | Create a run on **Payroll Runs** first. |
| "No daily rate is posted for this detachment" | **Detachments → + Post rate** with an effective date on or before the run's pay date, or switch on the employee's own rate. |
| A run shows a blocker for a missing tax table | No withholding table is in force for the period's frequency and pay date. Check **Statutory tables**, and note that bi-weekly has none. |
| An employee is missing from a run | They are inactive, archived, separated before the period, or were not ticked when the run was created. |
| Net pay flagged negative | Deductions exceed earnings. Check loans, standing deductions and adjustments, then recalculate. |
| Cannot approve a run | You submitted it, or you are signed in as Accounting. The Administrator must approve it. |
| Cannot edit an approved or posted run | That is by design. A run waiting for approval can be returned to draft. Correct a posted payroll with an **Adjustment** run (5.1), or with an adjustment on the next regular run. |
| "…is closed — its regular payroll has been posted" | A second regular run is refused on a closed period. To correct someone already paid, choose **Adjustment** as the kind of run. To pay someone the posted run left out, **Reopen** the period first (Part 4, Step 1). |
| "…is locked, which freezes the attendance inside its cut-off" | The pay period is locked. Go to **Payroll Setup → Pay calendar → Reopen**, correct the day, lock the period again and recalculate. |
| "That day has been locked by a posted payroll run" | A posted run has paid that day, so it cannot change. Correct the pay with an Adjustment run. |
| The **Set a new password** screen appears at sign-in | You signed in with the default password, a new account's first password or a temporary password. Choose your own password to continue. |
| Timesheets does not list a 13th-month or adjustment run | That is by design. Those runs pay no hours, so a timesheet keyed against them would never be paid. |
| Payroll summary shows fewer runs than expected | Your date range only partly covers some cut-offs. They are named in the report's warning. Widen the range. |
| Payroll summary puts old overtime under OT Regular | Payslips computed before overtime was split into Regular/Sunday/Holiday have one OT line. The report names them. |

---

## 10. Quick reference

**Default sign-in:** `admin` / `Admin@123`. You must replace it at the first sign-in. After that, use **Change password** in the top bar.

**Every cut-off, in one line:**

> Accounting: **New run → Timesheets / Attendance → Leave & loans → Lock period (optional) → Calculate → Adjust → Submit**  
> Administrator: **Approvals → Approve → Post → Back up now**  
> Either: **Payslips → Export whole run**, **Reports → Register, remittances, summary**

**Run states:** Draft → For approval → Approved → Posted. A draft can be discarded (Cancelled), and a run waiting for approval can be returned to draft.

**Pay period states:** Open → Locked (manual; freezes attendance) → Closed (automatic when a regular run is posted; blocks another regular run). **Reopen** returns a period to Open.

**Correcting a posted payroll:** create an Adjustment run for the same period. It pays only the adjustments you enter on it.
