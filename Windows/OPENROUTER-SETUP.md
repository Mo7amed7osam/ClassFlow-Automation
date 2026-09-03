# OpenRouter setup (Windows)

1. Run the updated WindowsUI build, open **AI Matching**.
2. Select **OpenRouter** in **AI provider** before entering the key.
3. Enter a fresh OpenRouter key locally. Never paste it into chat, source files, or logs.
4. Select or type a full model ID, such as `openai/gpt-4.1-mini`.
5. Click **Test & save securely** (or **Test connection**). Both use the typed key first; if empty, they use the saved configuration only when its provider matches.
6. A completed, schema-valid model answer is required before **Done — Valid**. HTTP 200 alone is insufficient. The synthetic test may incur API charges.
7. Select a roster group and observed names (or an attendance snapshot). Explicitly consent to sending uncertain names through OpenRouter to the selected model provider before running matching.

## Integration boundaries

- OpenRouter uses `https://openrouter.ai/api/v1/chat/completions`.
- OpenAI continues using its existing Responses adapter. The provider is explicit; requests never automatically fall back to another vendor.
- The new adapter supplies strict JSON schema and `provider.require_parameters=true`. Model/endpoint support is validated by the test, not assumed from the dropdown.
- Only one active AI configuration is stored. A successful test replaces it; failed tests preserve it. Provider, model and key are stored together in the Windows Credential Manager blob, not application JSON.
- The existing credential target name `ZoomAutoAdmit/AI/OpenAI` is retained for backwards-compatible reads. Its name is historical: the new blob records the actual provider. Old blobs without that field load as OpenAI. Use the new EXE, not an older build, after saving OpenRouter credentials.
- Switching provider clears the input PasswordBox, invalidates readiness and resets consent. A saved key is never reused for another provider. Changing model requires a fresh test.
- Error feedback distinguishes authentication (401), insufficient credits (402), permissions (403), missing model (404), limits (429), unsupported requests (400), provider failures and invalid/incomplete responses.
- Provider error bodies, keys and authentication headers are never logged. Production HTTP redirects are disabled; response size is bounded.
- Existing normalization, rule matching, alias memory, thresholds, review decisions, roster order, admission engines, scheduler and meeting lifecycle are unchanged.

## Validation

OpenRouter tests use synthetic HTTP responses, synthetic keys and isolated credential targets. They do not access the user's account or verify the key posted in chat.

`RedesignViewTests` also drives the real compiled WPF provider picker and key-entry button handlers with a fake AI service. Preview: `Windows/TestResults/OpenRouterUi/openrouter-setup.png` (test data, not a live provider result).

Build output: `Windows/src/ZoomAutoAdmit.WindowsUI/bin/OpenRouter/Release/net8.0-windows10.0.19041.0/ZoomAutoAdmit.WindowsUI.exe`.

Official API references:
- [OpenRouter quickstart](https://openrouter.ai/docs/quickstart)
- [Structured outputs and endpoint support](https://openrouter.ai/docs/guides/features/structured-outputs)

Validated 2026-08-31: Release build 0 warnings/errors; 567 tests passed across 8 Windows test projects (23 new OpenRouter cases; WindowsUI total 86). WPF test execution is serialized because its fixtures share process-wide logger subscribers and UI-affine collections; production logging code was not changed. Final reports are in `Windows/TestResults/OpenRouterFinalValidation`. `git diff --check` passed. No commit or push.
