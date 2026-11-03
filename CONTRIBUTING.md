# Contributing

## Reporting a desync

Attach the log from the bridge and the server covering the time of the desync, plus the aircraft profile and the simulator version in use. Note which control drifted and what each pilot saw.

## Contributing an aircraft profile

Add a YAML file under `profiles/`, compatible with the YourControls format, with a per-entry `class:` field. Name the file after the aircraft and variant, and state the sim version it was tested on in the pull request.

## Commit messages

Write commit messages in English. Use a short imperative subject line, and add a body when the reason for the change is not obvious from the subject.