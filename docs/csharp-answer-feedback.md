# Answer feedback from a local model (#166)

After a learner finishes the self-assessment on a [design-tradeoff](csharp-design-tradeoff.md) step, a model running in Ollama on the Mac
reads their written justification and says, for each rubric point, whether it was made and why, and which red flags the answer claimed.
It is **advisory**: the learner's own ticks set the grade and the review rating, and nothing the model says changes either. The text
never leaves the machine.

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
| Android | none yet: the phone's loopback is not the Mac. The home server's proxy is #167 |

The host registers `IAnswerGrader` only when the manifest sets `graderModel` and the platform is not Android. `DesignTradeoffStepView`
looks the grader up optionally, so an app or platform without one shows no feedback section at all.

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

## Not yet

- Phone feedback through the server proxy (#167), requesting feedback later for an answer given on the phone, and the explicit "Ask Claude"
  button (#168).
- Choosing the model with evidence (#169).
- Not verified in a running app: this is covered by unit and bUnit tests against a fake backend. Run Ollama with the manifest's model and
  answer a design-tradeoff step on the Mac to see real feedback.
