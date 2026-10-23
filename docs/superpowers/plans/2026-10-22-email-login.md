# Email login

User flow: Email only → continue → password login if the account has a password;
otherwise legacy transfer screen → optional new registration page. Google remains;
Apple/Microsoft/passkeys are excluded. One normalized email identifies one member.

1. Add isolated SQLite regression checks for branching, passwords, duplicate email,
   Google linking, cookie invalidation, referrals, and preservation of member records.
2. Add nullable EmailPasswordHash while preserving existing PasswordHash Google subjects.
   Implement password login/registration, server-side validation and throttling.
3. Replace the login screen with progressive server-rendered forms. Preserve referral
   and local return URL. Keep first Google linking of password accounts password-protected.
4. Add an authenticated admin transfer action retaining member ID and records, with
   public transfer instructions. Supply a schema migration; do not apply to live SQL.
5. Run existing Google and member checks, new checks, compile both apps and inspect views.

Updated requirement (2026-10-23): use the existing integer member ID for self-service
transfer. Query after submission by member ID and HasLoggedInNewSite=false only;
show the name, confirm ownership, set password and retain the member ID/history.
Do not filter by role, password or Google subject. Set HasLoggedInNewSite=true in
the successful binding transaction. Reject IDs already marked true, duplicate emails,
changed records and replay.
Only a mismatched name or unsuccessful lookup opens support with optional screenshot
upload; show exactly 「查無可轉移帳號」 for unsuccessful lookup. No extra not-found button.
Store validated screenshots privately and provide an authorized admin list/image view.
Known design limit: knowing an unclaimed ID suffices to claim it; confirming a displayed
name is not proof of ownership. The user explicitly selected this ID-based flow.
Ruling: Work in the existing paired repos, preserving unrelated uncommitted changes.
No commit, deployment, external message, or shared database mutation is authorized here.

Baseline: GoogleLoginChecks passed 30 checks. Existing SqlClient package advisory noted.

Completed: shared password storage/hash helper; Email-first server forms; Google/password
linking; authenticated admin migration; original user-supplied transfer screenshot and
legacy-site instructions; additive SQL/EF migration and deployment documentation.

Verification: 30 existing Google checks plus Email service/HTTP checks passed. Coverage
includes Google-first password setup, referral rollback, cookie invalidation, CSRF,
registration collision, local return URLs and attempt throttling. Admin's 272 existing
checks plus transfer preservation/replay tests passed. Both applications compiled.
Browser: inspected desktop and 390px mobile flow; original guide loaded at 976px width,
no horizontal overflow, no-code button opens registration. Preview used SQLite only
and was stopped after inspection. Screenshot: .codex-artifacts/email-login/transfer-desktop.jpg
at the parent workspace root.

Independent review: no functional/critical defects; requested password-setup coverage
was added and passed. Existing unrelated edits were preserved; no commit/deploy/live
SQL mutation. Google OAuth live sign-in and SQL Server migration remain unexecuted.

2026-10-23 follow-up completed: staged automatic transfer, name confirmation, missing-ID
support, optional validated screenshot persistence and authorized backend case/image view.
Latest verification: Google/Email service and HTTP checks passed, including missing/mismatched
accounts, uploads, invalid/oversized images, idempotence, staged/tampered/replayed tickets,
all transfer CSRF boundaries, duplicate emails, preserved member history and referral back links.
Admin checks passed (273 existing assertions plus transfer/support checks); Razor compiled.
Browser verified confirmation/support/missing-ID screens and 390px mobile layout without
horizontal overflow. Screenshot: .codex-artifacts/email-login/transfer-not-found-mobile.jpg.
Independent reviewer found a lost referral on return links; fixed and regression-tested.
Preview stopped; no live database updates, deployment or external messages performed.
