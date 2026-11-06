# Changelog

What changed in each version. `publish-release.ps1` copies the section for the
version being published into the GitHub release, and the app shows that text
under **Help & Updates → What's new** — so write it for the payroll staff, not
for developers. Name the request each change answers (`REQ-…`).

Format for a new version (newest at the top, heading must be `## <version>`):

```
## 1.2

### Fixed
- Night differential now counts the hour after midnight (REQ-261012-0915).

### Changed
- ...
```

## 1.3

### Fixed
- **Dark mode is dark everywhere.** Dropdown lists, date-picker calendars,
  switches, "Are you sure?" messages and the window's title bar used to follow
  the Windows setting instead of **Settings → Theme**, so they could stay light
  in dark mode (or dark in light mode). They now follow the app's theme.
- Selected rows in **Payroll Runs**, **Approvals**, **Payslips** and
  **Reports**, report total rows, flagged rows and report warnings no longer
  show a light background with unreadable text in dark mode.
- Net pay in a run's employee list is readable in dark mode.
- Switches that are on keep their white knob after changing the theme, and
  show it in light mode too.

## 1.2

### New
- **Payroll Officer** accounts. An administrator can now choose *Payroll
  Officer* as the role in **User Accounts**. A Payroll Officer enters
  timesheets, attendance and leave, computes payroll runs, and opens payslips
  and reports. They cannot change employees, detachment rates or Payroll
  Setup, and they cannot approve a run. Accounting and Administrator accounts
  work the same as before.
- **Updates announce themselves.** While the PC is connected to the internet,
  the app looks for a new version every half hour (and again soon after the
  internet comes back). When one is found, a blue strip appears at the top:
  click **Download** to fetch it while you keep working, then an administrator
  clicks **Install** when it is a good moment. Nothing is downloaded or
  installed until someone chooses to. If the internet drops during the
  download, it carries on by itself once the connection is back.
- **Notes** box at the bottom of each employee's record, for anything the
  record has no field for yet. It holds about 400 words, keeps line breaks,
  and shows a word count as you type. Notes are never printed on payslips or
  reports.

### Fixed
- Editing an archived employee (from the **Archived** filter) no longer
  restores them to the lists by accident.

## 1.1

### New
- **Help & Updates** screen. The app checks for a new version every time
  someone signs in and shows "Update available" at the top. An administrator
  clicks **Back up and install**. The app backs up the database, installs the
  new version and opens again. Nothing needs to be downloaded or sent by hand.
- **Report a problem or request a change** form under Help & Updates. It saves
  one zip file (with a reference such as `REQ-261006-1430`) to send to the
  developer by Messenger, e-mail or Google Drive.

### Note
- This is the last version that has to be installed by hand. Later versions
  arrive through Help & Updates.

## 1.0

- First version installed at the company.
