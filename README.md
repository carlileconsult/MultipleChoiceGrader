# MultipleChoiceGrader (Job-based + configurable paths)

## Job folder structure (existing style)

```
jobs/
  sample-job/
    answer-key.json
    settings.json
    submissions/
    results/
```

The app writes extracted output to `results/extracted-json/`.

## appsettings.json configuration

```json
{
  "Grading": {
    "DefaultJobPath": "jobs/sample-job",
    "AnswerKeyPath": "jobs/sample-job/answer-key.json",
    "SubmissionsPath": "jobs/sample-job/submissions",
    "ResultsPath": "jobs/sample-job/results",
    "ExtractorMode": "Hybrid",
    "UseCache": true
  }
}
```

Path precedence is:
1. Command-line explicit path arguments (`--answer-key`, `--submissions`, `--results`)
2. `appsettings.json` values
3. Job-folder defaults (`<job>/answer-key.json`, `<job>/submissions`, `<job>/results`)

If `--job` is not provided, `Grading:DefaultJobPath` is used.

## Command examples

### A) Existing job-folder style

- `dotnet run -- --job jobs/sample-job`
- `dotnet run -- --job jobs/sample-job --extract-only`
- `dotnet run -- --job jobs/sample-job --grade-only`
- `dotnet run -- --job jobs/sample-job --use-cache`

### B) appsettings.json style

- `dotnet run`

Using:

```json
{
  "Grading": {
    "DefaultJobPath": "jobs/sample-job",
    "AnswerKeyPath": "jobs/sample-job/answer-key.json",
    "SubmissionsPath": "jobs/sample-job/submissions",
    "ResultsPath": "jobs/sample-job/results",
    "ExtractorMode": "Hybrid"
  }
}
```

### C) Command-line override style

- `dotnet run -- --answer-key "C:\\Tests\\Test1\\answer-key.json" --submissions "C:\\Tests\\Test1\\submissions" --results "C:\\Tests\\Test1\\results"`

## Behavior notes

- Relative paths are resolved from the current working directory.
- Absolute paths are used as-is.
- Answer key file must exist.
- Submissions folder must exist.
- Results folder is created automatically if missing.
- `results/extracted-json` is created automatically if missing.

## Extraction vs grading

- **Extraction stage** reads from the resolved submissions path and writes JSON into `ResultsPath/extracted-json`.
- **Grading stage** reads from the resolved answer key path and extracted JSON files, then writes reports to `ResultsPath`.
- AI is only for extraction/OCR fallback, never grading.
