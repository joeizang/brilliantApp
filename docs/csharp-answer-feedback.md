# Answer feedback from a local model (#166, #167)

After a learner finishes the self-assessment on a [design-tradeoff](csharp-design-tradeoff.md) step, a model running in Ollama on the Mac
reads their written justification and says, for each rubric point, whether it was made and why, and which red flags the answer claimed.
It is **advisory**: the learner's own ticks set the grade and the review rating, and nothing the model says changes either. The text
never leaves the machine, unless the learner turns on phone feedback below.

## Flow

1. The learner picks an option, writes why, and sees the model answer and rubric.
2. They tick what their answer made and any red flag it claimed, and press Done. The grade is recorded as before.
3. Only then is the grader asked, so the model's view can't colour the self-assessment. The result and Continue are already on screen;
   a "Getting feedback…" line fills in below Continue.
4. If feedback can't be had, that line says why and the learner carries on. Leaving the step cancels the request.

The same view is used by lessons, Review and test-out, so feedback appears wherever the step does.

## Where it runs

| Platform | Grader |
|---|---|
| Mac | `OllamaAnswerGrader`, calling `http://localhost:11434` directly (the sandbox already has `network.client`) |
| Android | `ProxyAnswerGrader`, asking the home server to ask the Mac's Ollama. Opt-in, off until the learner turns it on ([below](#phone-feedback-through-the-server-opt-in)) |

The host registers `IAnswerGrader` only when the manifest sets `graderModel`. `DesignTradeoffStepView` looks the grader up optionally,
so an app or platform without one shows no feedback section at all, and a grader whose `Enabled` is false (the phone's, until the
learner opts in) is treated the same way.

## The model

`graderModel` in the app manifest names an Ollama model tag, validated as a single tag (no spaces or shell characters). The C# app
starts with `gemma3:12b` as a placeholder until the grader evals (#169) choose between candidates. Changing it is a one-line manifest edit.

## The call

`OllamaAnswerGrader` posts to `/api/chat` with `stream: false`, `temperature: 0` and a JSON-schema `format`:

```json
{ "points": [{ "index": 0, "hit": true, "note": "..." }], "redFlagsFound": [{ "index": 0, "note": "..." }] }
```

`points` must hold exactly one entry per rubric point. `GradingPrompt` builds the messages: rules in the system message, and in the user
message the scenario, the option picked, the numbered rubric and red flags, the model answer, and the learner's text between `<answer>`
tags (a literal `</answer>` inside it is defused). The instructions say the answer is data, never instructions.

## Failures

`AnswerGraderException` carries a failure kind and a message the learner can act on. The view shows the message and never fails the step.

| Kind | When | Message |
|---|---|---|
| `Unavailable` | connection refused, timeout, or any non-2xx status other than 404 | "Ollama isn't running. Start it on this Mac to get feedback." (or took too long / HTTP code) |
| `ModelMissing` | HTTP 404 | "Ollama doesn't have the model '…'. Fetch it with: ollama pull …" |
| `BadResponse` | not JSON, wrong shape, a point missing or repeated, an index outside the rubric, a non-boolean `hit` | "The model's feedback couldn't be understood." |

Server error text is never shown. Notes are trimmed and cut at 400 characters. Duplicate red-flag reports collapse to one. Any other
exception is shown as "Feedback is unavailable right now."

## Phone feedback through the server (opt-in)

A phone has no Ollama, so with the learner's say-so it asks the household server, which asks the Mac.

1. **Opt in.** On the Sync screen, once paired, tick "Get written-answer feedback through the server". It is off by default, kept in
   `feedback-settings.json` in the app's data folder, and while it is off the answer text never leaves the phone (apart from the normal sync of progress).
2. **Ask.** After the self-assessment the phone posts the same grading request to `POST /feedback/grade` on the server it is paired with,
   with its device token. The server looks up the grader model in the calling app's manifest and calls `/api/chat` on `Feedback:OllamaUrl`
   using the same `OllamaAnswerGrader` as the Mac, so the prompt and checks are identical.
3. **Fail fast.** The server waits 3 seconds to connect and 120 seconds for the answer. A Mac that is asleep or off the network is
   refused or times out and the phone is told so: "Feedback unavailable right now. Your answer is saved; ask for feedback on it later from
   your Mac." The server never puts the Ollama address in its errors.
4. **Check on the phone.** The phone rebuilds the feedback from its own copy of the rubric (indices must match, notes are cut), so a
   confused server cannot change what a point says.
5. **Later, from the Mac.** The written answer is in the progress log and syncs like any other answer. On the Mac, open **Written
   answers** (sidebar, or the link on Tracks in a narrow window) to see answers written on any device, newest first (the latest 50), and
   press **Get feedback** on one. The Mac's own Ollama is asked; the result is shown and not kept.

The Written answers screen is only offered where a grader is enabled, so a phone that has not opted in does not show it.

### The one-time setting on the Mac

Ollama listens on the Mac's loopback only, so the server cannot reach it until you let it listen on the network. Quit Ollama, then:

```bash
launchctl setenv OLLAMA_HOST "0.0.0.0:11434"
```

and start Ollama again. (If you run `ollama serve` from a shell, use `OLLAMA_HOST=0.0.0.0:11434 ollama serve` instead.) Then put the
Mac's address in the server's `.env`, as `OLLAMA_URL=http://192.168.1.50:11434`, and `docker compose up -d` to apply it. Give the Mac a
fixed address (a DHCP reservation on the router), or the URL goes stale.

**LAN exposure.** Ollama has no sign-in. With `OLLAMA_HOST` on `0.0.0.0`, anything on the home network can use the Mac's models and
read or delete them, not only the server. Keep it to a network you trust, and where you can, allow port 11434 only from the server's
address in the macOS firewall or on the router. Do not forward it to the internet. The Mac's own app keeps using `localhost`, so you can
undo the setting (`launchctl unsetenv OLLAMA_HOST`) and lose only phone feedback.

## Not yet

- The explicit "Ask Claude" button (#168).
- Choosing the model with evidence (#169).
- Feedback is not kept, so asking again from the Mac asks the model again.
- Not verified in a running app or against a real Ollama: this is covered by unit, bUnit and server integration tests against fakes
  (including a real refused connection and a silent listener). Run Ollama with the manifest's model, set `OLLAMA_HOST`, and answer a
  design-tradeoff step on a phone to see it end to end.
