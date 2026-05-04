# MultipleChoiceGrader (Job-based)

## Job folder structure

```
jobs/
  sample-job/
    answer-key.json
    settings.json
    submissions/
    results/
```

The app writes extracted output to `results/extracted-json/`.

## Answer key format

```json
{
  "assignmentName": "Biology Chapter 5 Test",
  "questions": {
    "1": "A",
    "2": "C",
    "3": "B"
  }
}
```

## Settings format

```json
{
  "extractorMode": "Hybrid",
  "validChoices": ["A", "B", "C", "D"],
  "allowBlankAnswers": true,
  "needsReviewWhenAiUsed": false,
  "needsReviewWhenConfidenceBelow": "high",
  "studentNameFallback": "FileName"
}
```

If `settings.json` is missing, defaults are created automatically.

## Command examples

- `dotnet run -- --job jobs/sample-job`
- `dotnet run -- --job jobs/sample-job --extract-only`
- `dotnet run -- --job jobs/sample-job --grade-only`
- `dotnet run -- --job jobs/sample-job --use-cache`
- `dotnet run -- --job jobs/sample-job --force-reextract`

## Extraction vs grading

- **Extraction stage** reads documents and produces one JSON per submission in `results/extracted-json/`.
- **Grading stage** reads `answer-key.json` and extracted JSON files, then calculates scores in code.
- AI is only for extraction/OCR fallback, never grading.

## Limitations and review guidance

- Current AI extraction function is a stub; wire this to your OCR/AI provider.
- Rule-based extraction expects text patterns like `1: A` and `Name: Jane Doe`.
- `review-needed.csv` should be used for manual quality checks where confidence/warnings indicate uncertainty.
- For scanned PDFs/images, validate extraction confidence before using grades operationally.
