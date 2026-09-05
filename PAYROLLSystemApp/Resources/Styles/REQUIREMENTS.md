# Payroll Management System — Requirements Specification

**Project:** PAYROLLSystemApp
**Platform:** .NET MAUI 10 (Android, iOS, macOS Catalyst, Windows)
**Document version:** 1.0
**Date:** 2026-08-30
**Status:** Draft

---

## 1. Introduction

### 1.1 Purpose
This document defines the functional and non-functional requirements for the **Payroll Management System (PMS)** — a cross-platform application that automates employee record keeping, time and attendance tracking, payroll computation, statutory deductions, payslip generation, and payroll reporting.

### 1.2 Scope
The system replaces manual, spreadsheet-based payroll processing. It covers the full payroll cycle:

`Employee onboarding → Attendance capture → Payroll run → Review & approval → Payslip release → Reporting & remittance`

**In scope:** employee master data, attendance/timekeeping, leave balances, earnings and deductions, payroll runs, payslips, government-mandated contributions and withholding tax, payroll reports, user roles and access control.

**Out of scope (Phase 1):** direct bank API disbursement, biometric hardware integration, recruitment/ATS, performance appraisal, full general-ledger accounting.

### 1.3 Definitions and Acronyms

| Term | Meaning |
|---|---|
| PMS | Payroll Management System |
| Payroll Run | One complete computation cycle for a pay period |
| Cut-off | Date range of attendance included in a payroll run |
| Gross Pay | Total earnings before deductions |
| Net Pay | Take-home pay after all deductions |
| Statutory Deduction | Government-mandated contribution (SSS, PhilHealth, Pag-IBIG, Withholding Tax) |
| De Minimis | Non-taxable benefits within legal ceilings |
| 13th Month Pay | Legally mandated additional annual compensation |
| RBAC | Role-Based Access Control |

### 1.4 Assumptions
- **A-01:** The system computes Philippine statutory deductions (SSS, PhilHealth, Pag-IBIG, BIR withholding tax). All rates and brackets are stored as **editable configuration data, not hard-coded**, so the system can be re-tabled when the law changes or localized to another jurisdiction.
- **A-02:** Default pay frequency is **semi-monthly** (1st–15th, 16th–end of month); weekly, bi-weekly, and monthly are configurable.
- **A-03:** Attendance is captured in-app (manual entry or in/out punch). Biometric device import is a future integration.
- **A-04:** Currency is PHP by default and is configurable.

### 1.5 Actors / User Roles

| Role | Description |
|---|---|
| **System Administrator** | Manages users, roles, system configuration, backups, and audit logs |
| **HR Officer** | Manages employee records, attendance corrections, leave, and company setup |
| **Payroll Officer** | Executes payroll runs, adjustments, and generates payslips and reports |
| **Approver / Manager** | Reviews and approves payroll runs, leave, and overtime requests |
| **Employee** | Views own profile, attendance, leave balance, and payslips (self-service) |

---

## 2. Functional Requirements

Priority legend: **M** = Must have · **S** = Should have · **C** = Could have

### 2.1 Authentication and Access Control

| ID | Requirement | Priority |
|---|---|---|
| FR-001 | The system shall authenticate users via username/email and password before granting access. | M |
| FR-002 | The system shall enforce role-based access control; a user shall only see screens and data permitted by their assigned role. | M |
| FR-003 | The system shall lock an account after 5 consecutive failed login attempts and require administrator reset or a timed unlock. | M |
| FR-004 | The system shall allow users to change their own password and shall enforce the password policy in NFR-013. | M |
| FR-005 | The system shall support secure password reset through an administrator-issued temporary credential. | S |
| FR-006 | The system shall automatically sign out a session after a configurable period of inactivity (default 15 minutes). | M |
| FR-007 | The system shall support biometric/device unlock (fingerprint, face) on mobile platforms as a secondary convenience factor. | C |

### 2.2 Employee Management

| ID | Requirement | Priority |
|---|---|---|
| FR-010 | The system shall allow HR to create, view, update, and deactivate employee records. | M |
| FR-011 | Each employee record shall capture: employee number, full name, birth date, gender, civil status, contact number, email, address, emergency contact, and photo. | M |
| FR-012 | Each employee record shall capture employment data: date hired, employment status (Regular, Probationary, Contractual, Part-time), department, position, immediate supervisor, and work schedule. | M |
| FR-013 | Each employee record shall capture compensation data: pay type (Monthly, Daily, Hourly), basic rate, allowances, and pay frequency. | M |
| FR-014 | Each employee record shall capture government IDs: SSS, PhilHealth, Pag-IBIG, and TIN, and shall validate their format. | M |
| FR-015 | The system shall enforce a unique employee number and reject duplicates. | M |
| FR-016 | The system shall support employee search and filtering by name, employee number, department, position, and status. | M |
| FR-017 | The system shall retain deactivated (resigned/terminated) employees as read-only historical records and exclude them from new payroll runs. | M |
| FR-018 | The system shall maintain a change history of salary rate adjustments with effective dates. | S |
| FR-019 | The system shall support bulk import of employee records from CSV/Excel with validation and an error report. | C |

### 2.3 Time and Attendance

| ID | Requirement | Priority |
|---|---|---|
| FR-020 | The system shall record daily time-in and time-out per employee. | M |
| FR-021 | The system shall compute per-day rendered hours, late minutes, undertime, and absences against the assigned work schedule. | M |
| FR-022 | The system shall compute overtime hours separately from regular hours, subject to approval. | M |
| FR-023 | The system shall classify hours by premium type: regular, overtime, night differential, rest day, regular holiday, and special non-working holiday. | M |
| FR-024 | The system shall allow HR to correct or manually override attendance entries, recording the reason, the editor, and the timestamp. | M |
| FR-025 | The system shall maintain a configurable company holiday calendar with holiday type per date. | M |
| FR-026 | The system shall support configurable grace periods for tardiness. | S |
| FR-027 | The system shall generate an attendance summary per employee per cut-off period. | M |
| FR-028 | The system shall support importing attendance logs from an external biometric device file (CSV). | C |

### 2.4 Leave Management

| ID | Requirement | Priority |
|---|---|---|
| FR-030 | The system shall maintain leave types (Vacation, Sick, Emergency, Maternity/Paternity, Service Incentive Leave, Unpaid) with configurable annual credits. | M |
| FR-031 | The system shall allow employees to file leave requests and shall route them to an approver. | S |
| FR-032 | The system shall track leave credits earned, used, and remaining per employee per year. | M |
| FR-033 | The system shall distinguish paid from unpaid leave and apply the correct treatment during payroll computation. | M |
| FR-034 | The system shall prevent approval of leave that exceeds the available credits of an employee unless explicitly overridden as unpaid. | S |

### 2.5 Payroll Configuration

| ID | Requirement | Priority |
|---|---|---|
| FR-040 | The system shall allow an administrator to define pay periods, cut-off dates, and pay dates. | M |
| FR-041 | The system shall allow definition of earning types (basic pay, overtime, allowance, bonus, commission, holiday pay, night differential) and flag each as taxable or non-taxable. | M |
| FR-042 | The system shall allow definition of deduction types (statutory, loan, cash advance, tardiness, absence, other) and their computation method. | M |
| FR-043 | The system shall store statutory contribution tables (SSS, PhilHealth, Pag-IBIG) and withholding tax brackets as editable, effective-dated configuration. | M |
| FR-044 | The system shall allow configuration of overtime, night differential, rest day, and holiday premium multipliers. | M |
| FR-045 | The system shall store company profile information (name, address, TIN, employer registration numbers, logo) for use on payslips and reports. | M |

### 2.6 Payroll Processing

| ID | Requirement | Priority |
|---|---|---|
| FR-050 | The system shall allow a Payroll Officer to create a payroll run for a selected pay period and set of employees. | M |
| FR-051 | The system shall compute **gross pay** as: basic pay for rendered days/hours + overtime + holiday premiums + night differential + allowances + other taxable and non-taxable earnings. | M |
| FR-052 | The system shall compute deductions for tardiness, undertime, and absences based on the daily/hourly rate of the employee. | M |
| FR-053 | The system shall compute SSS, PhilHealth, and Pag-IBIG employee contributions and the corresponding employer shares using the effective-dated tables. | M |
| FR-054 | The system shall compute withholding tax on taxable income using the effective-dated tax brackets for the pay frequency. | M |
| FR-055 | The system shall apply recurring deductions such as salary loans, cash advances, and company loans, and decrement the outstanding balance each period. | M |
| FR-056 | The system shall compute **net pay** as gross pay less total deductions, and shall never produce a negative net pay without flagging it for review. | M |
| FR-057 | The system shall allow one-off adjustments (additional earnings or deductions) on a specific payroll run with a required remark. | M |
| FR-058 | The system shall support payroll run states: **Draft → For Approval → Approved → Posted**, and shall prevent editing of an Approved or Posted run. | M |
| FR-059 | The system shall allow a Draft run to be recalculated or discarded without affecting prior periods. | M |
| FR-060 | The system shall prevent creating a duplicate payroll run for the same employee and pay period. | M |
| FR-061 | The system shall compute 13th month pay based on total basic salary earned within the calendar year. | S |
| FR-062 | The system shall compute final pay for separated employees, including unused leave conversion and pro-rated 13th month pay. | S |
| FR-063 | The system shall round all monetary values to two decimal places using a consistent, documented rounding rule. | M |

### 2.7 Payslips

| ID | Requirement | Priority |
|---|---|---|
| FR-070 | The system shall generate an itemized payslip per employee per payroll run showing employee details, pay period, all earnings, all deductions, gross pay, and net pay. | M |
| FR-071 | The system shall export payslips to PDF. | M |
| FR-072 | The system shall allow employees to view and download their own payslip history through self-service. | S |
| FR-073 | The system shall support emailing payslips to employees. | C |
| FR-074 | The system shall display year-to-date totals for gross pay, taxable income, tax withheld, and statutory contributions on the payslip. | S |

### 2.8 Reporting

| ID | Requirement | Priority |
|---|---|---|
| FR-080 | The system shall generate a **payroll register** listing all employees and their earnings, deductions, and net pay for a payroll run. | M |
| FR-081 | The system shall generate **statutory remittance reports** for SSS, PhilHealth, Pag-IBIG, and withholding tax, showing employee and employer shares. | M |
| FR-082 | The system shall generate a **bank disbursement / payroll summary file** listing employee account details and net pay amounts. | S |
| FR-083 | The system shall generate an **attendance and absences report** per department and period. | S |
| FR-084 | The system shall generate an **annual alphalist / employee tax summary** for year-end reporting. | S |
| FR-085 | The system shall export all reports to PDF and Excel/CSV. | M |
| FR-086 | The system shall present a dashboard summarizing headcount, current-period payroll cost, pending approvals, and upcoming pay dates. | S |

### 2.9 Administration and Audit

| ID | Requirement | Priority |
|---|---|---|
| FR-090 | The system shall allow an administrator to create, edit, deactivate, and assign roles to user accounts. | M |
| FR-091 | The system shall write an audit log entry for every create, update, delete, approval, and login event, recording the user, timestamp, action, and affected record. | M |
| FR-092 | Audit log entries shall be immutable and viewable only by administrators. | M |
| FR-093 | The system shall support manual and scheduled database backup and restore. | S |
| FR-094 | The system shall provide a data archival function for closed payroll years. | C |

---

## 3. Non-Functional Requirements

### 3.1 Performance

| ID | Requirement | Target |
|---|---|---|
| NFR-001 | Screen navigation and list rendering shall complete within 2 seconds under normal load. | ≤ 2 s |
| NFR-002 | A payroll run for 500 employees shall complete within 60 seconds. | ≤ 60 s |
| NFR-003 | Employee search results shall return within 1 second for a 5,000-record dataset. | ≤ 1 s |
| NFR-004 | PDF payslip generation shall complete within 3 seconds per payslip and support batch generation in the background. | ≤ 3 s |
| NFR-005 | Application cold start shall not exceed 4 seconds on a mid-range device. | ≤ 4 s |

### 3.2 Scalability and Capacity

| ID | Requirement |
|---|---|
| NFR-006 | The system shall support at least 5,000 active employee records without degradation of the targets in §3.1. |
| NFR-007 | The system shall retain at least 5 years of payroll history online. |
| NFR-008 | The system shall support at least 50 concurrent users in the shared-database deployment. |

### 3.3 Reliability and Availability

| ID | Requirement |
|---|---|
| NFR-009 | Payroll computation shall be **deterministic** — re-running the same period with the same inputs shall yield identical results. |
| NFR-010 | A payroll run shall be **transactional**: a failure mid-run shall leave no partial results committed. |
| NFR-011 | The system shall target 99% availability during business hours for server-backed deployments. |
| NFR-012 | The system shall recover gracefully from unexpected termination without corrupting the local database, and shall not lose committed data. |

### 3.4 Security

| ID | Requirement |
|---|---|
| NFR-013 | Passwords shall be at least 8 characters with upper case, lower case, a digit, and a special character. |
| NFR-014 | Passwords shall be stored only as salted hashes using a modern algorithm (BCrypt/Argon2/PBKDF2). Plaintext or reversible storage is prohibited. |
| NFR-015 | All network traffic shall use TLS 1.2 or higher. |
| NFR-016 | Sensitive local data (tokens, credentials, cached payroll data) shall be stored using platform secure storage / encrypted storage. |
| NFR-017 | Salary and payroll data shall be visible only to the owning employee and to authorized HR, Payroll, and Admin roles. |
| NFR-018 | The system shall be resistant to SQL injection through exclusive use of parameterized queries or an ORM. |
| NFR-019 | The system shall comply with the Data Privacy Act of 2012 (RA 10173) regarding collection, storage, retention, and disposal of personal data. |
| NFR-020 | Government IDs and bank account numbers shall be masked in list views and reports where full display is not required. |

### 3.5 Usability

| ID | Requirement |
|---|---|
| NFR-021 | A new HR user shall be able to complete a payroll run after no more than 1 hour of training with the user manual. |
| NFR-022 | All destructive actions (delete, discard run, deactivate employee) shall require explicit confirmation. |
| NFR-023 | Input validation errors shall be shown inline, next to the offending field, in plain language. |
| NFR-024 | The interface shall use consistent typography, spacing, and color defined centrally in `Resources/Styles/Styles.xaml` and `Colors.xaml`. |
| NFR-025 | The application shall support both light and dark themes and follow the device system setting. |
| NFR-026 | Long-running operations shall display progress feedback and shall not block the UI thread. |

### 3.6 Compatibility and Portability

| ID | Requirement |
|---|---|
| NFR-027 | The application shall run on Android 5.0 (API 21) and above, iOS 15+, macOS Catalyst 15+, and Windows 10 build 17763 and above, from a single .NET MAUI codebase. |
| NFR-028 | Layouts shall be responsive and usable on phone, tablet, and desktop window sizes without horizontal scrolling. |
| NFR-029 | The data access layer shall be abstracted so the storage engine (e.g. SQLite local, SQL Server shared) can be changed without altering business logic. |

### 3.7 Maintainability

| ID | Requirement |
|---|---|
| NFR-030 | The application shall follow the **MVVM** pattern with a clear separation of Models, ViewModels, Views, and Services. |
| NFR-031 | Statutory rates, brackets, multipliers, and thresholds shall be data-driven and updatable without recompiling the application. |
| NFR-032 | Payroll computation logic shall be covered by automated unit tests, including boundary cases for each tax and contribution bracket. |
| NFR-033 | The system shall log errors with sufficient context (timestamp, user, operation, stack trace) for diagnosis, without logging sensitive personal data. |
| NFR-034 | Code shall follow standard C#/.NET naming and style conventions and be documented at the public API level. |

### 3.8 Accuracy and Integrity

| ID | Requirement |
|---|---|
| NFR-035 | All monetary computations shall use a decimal (fixed-point) type, never floating point. |
| NFR-036 | Computed net pay shall match a manual computation to the centavo for all defined test scenarios. |
| NFR-037 | Posted payroll records shall be immutable; corrections shall be made through a documented adjustment entry, never by editing history. |
| NFR-038 | Referential integrity shall be enforced: a payroll record cannot exist without a valid employee and pay period. |

### 3.9 Localization

| ID | Requirement |
|---|---|
| NFR-039 | Currency, date, and number formatting shall follow the configured locale, defaulting to `en-PH` and PHP. |
| NFR-040 | UI text shall be externalized into resource files to permit future translation. |

---

## 4. Requirement Traceability Summary

| Module | Functional IDs | Key Non-Functional IDs |
|---|---|---|
| Authentication & Access | FR-001 – FR-007 | NFR-013 – NFR-017 |
| Employee Management | FR-010 – FR-019 | NFR-006, NFR-019, NFR-020 |
| Time & Attendance | FR-020 – FR-028 | NFR-001, NFR-003 |
| Leave Management | FR-030 – FR-034 | NFR-023 |
| Payroll Configuration | FR-040 – FR-045 | NFR-031 |
| Payroll Processing | FR-050 – FR-063 | NFR-002, NFR-009, NFR-010, NFR-035 – NFR-038 |
| Payslips | FR-070 – FR-074 | NFR-004 |
| Reporting | FR-080 – FR-086 | NFR-005, NFR-039 |
| Administration & Audit | FR-090 – FR-094 | NFR-011, NFR-012, NFR-018 |

---

## 5. Constraints

- **C-01:** Built with .NET MAUI 10 targeting Android, iOS, macOS Catalyst, and Windows from one codebase.
- **C-02:** Statutory computations must reflect the contribution and tax tables in force on the pay date; historical runs must retain the rates that were in force at that time.
- **C-03:** Phase 1 uses a local SQLite database per installation; a shared server database is a Phase 2 concern.
- **C-04:** No third-party service may receive employee personal data without a documented processing agreement.

## 6. Acceptance Criteria (High Level)

1. An HR user can onboard an employee, record a full cut-off of attendance, and produce a correct payslip end to end.
2. Statutory deductions and withholding tax match the official reference tables for every bracket boundary tested.
3. An approved payroll run cannot be edited, and every change attempt is captured in the audit log.
4. All Must-have (**M**) requirements are implemented and demonstrated.
5. The application builds and runs on at least Windows and Android targets.

---

## 7. Revision History

| Version | Date | Author | Changes |
|---|---|---|---|
| 1.0 | 2026-08-30 | — | Initial draft of functional and non-functional requirements |
