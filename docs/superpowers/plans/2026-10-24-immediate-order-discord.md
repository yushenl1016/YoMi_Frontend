# Immediate order Discord notifications

User-selected behavior: after an order transaction commits, immediately wake the existing frontend notification worker. The order response must not wait for Discord. The durable queue, leases, rate limits, retry delays, and 15-second recovery scan stay in place. Employee payment notifications retain their existing trigger.

The selected approach adds a shared, coalescing wake signal between the order controller and the worker. Calling Discord in the checkout request would make checkout depend on Discord; shortening polling alone would still add avoidable waiting.

Implementation plan (in the existing checkout, preserving unrelated edits):

- [x] Run the existing disposable LocalDB OrderChecks baseline.
- [x] Add an HTTP regression check: a committed order wakes an idle worker within three seconds, and a blocked Discord response does not block checkout. Observe it fail first.
- [x] Register a singleton wake signal, publish it after successful CreateAsync, and let the worker wait for a signal or its existing recovery interval.
- [x] Update the test fixture registrations and notification documentation.
- [x] Run OrderChecks and review the scoped diff. Use fake Discord HTTP and disposable LocalDB only. OrderChecks passed (253 PASS lines); FrontendSettingsChecks also passed after updating its manual checkout fixture. Independent review approved the scoped diff with no findings.

The change cannot guarantee that Discord accepts every message within five seconds during network failures, rate limits, or downtime. No production deployment or genuine Discord message is part of this change.
