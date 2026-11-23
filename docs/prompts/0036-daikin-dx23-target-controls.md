# Investigate missing Daikin target temperature controls for dx23 devices

Date: 2026-11-23

## Original prompt

Investigate missing Daikin target temperature controls for dx23 devices.



Current behavior:

- Sturgel Kitchen (dx23): Heating, target 16°C, online, writes enabled, but no temperature controls.
- Sturgel Room 1 (dx23): Heating, target 22°C, online, writes enabled, but no temperature controls.
- Sturgel Big Room (dx4) and Sturgel Small room 2 (dx4) have working temperature controls.

Tasks:

1. Compare actual ONECTA capability metadata and API responses for dx23 versus dx4.
2. Find why dx23 exposes target temperature for reading but not writing.
3. Check whether dx23 uses a different writable setpoint path, operation mode mapping, or capability structure.
4. If the official API confirms writable temperature support, implement the correct mapping and display the same +/- and Apply target controls.
5. Respect device-specific temperature ranges and increments.
6. Preserve authorization, existing write protection, readback verification and rate limits.
7. If the API does not expose writable setpoint support, explain the exact limitation rather than inventing controls.
8. Add tests for both dx23 and dx4.
9. Run builds/tests, archive the prompt and commit locally. Do not push.
10. Do not send live device commands during implementation.

Keep changes focused on Daikin temperature capabilities.

## Execution summary

One quota-accounted, uncached ONECTA discovery GET matched the four requested devices to their stored bindings. All four expose room targets on the same heating and cooling setpoint paths. The dx23 units advertise writable setpoints with 0.5 increments and omit the unit. The named dx4 units explicitly report Celsius and have slightly wider ranges. A separate bound dx4 unit reported explicit Celsius for the same heating and cooling ranges observed on dx23.

The mapper now recognizes those verified unit-omitting dx23 ranges on writable air-conditioner setpoints. The existing controls display 10–30 °C for heating and 18–32 °C for cooling after the next sync. Tests cover both models, bounds, half-degree stepping, unknown or nonwritable metadata, the mocked PATCH path, and readback. Both builds passed, with 157 backend tests passed, seven opt-in database tests skipped, and 33 frontend tests passed.

The detailed developer portal contract requires authentication. The evidence used here came from the official live discovery response and a comparison with explicitly Celsius metadata. No live PATCH or device command was sent.

- [ONECTA model comparison](../integrations/daikin-onecta.md#dx23-and-dx4-room-targets)
- [Daikin room-target mapping](../../src/backend/HomeAutomation.Infrastructure/Daikin/DaikinMapping.cs)
