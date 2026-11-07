# AgentRAG recording walkthrough

Status: planned, not recorded. A screenshot or this script is not a demo video.

Use only the dedicated reviewer account and the two synthetic Atlas fixture documents. Keep passwords, tokens, personal browser tabs and unrelated user data off screen.

1. Start recording with a screen recorder. On Windows, an available option is the Snipping Tool's video-recording mode. Computer control can demonstrate the flow once recording is active; no video-recording tool is currently exposed to this chat.
2. In Codex with the development AgentRAG plugin selected, show the authenticated connection and the plugin version being submitted. Complete login before exposing any credentials to the recording.
3. Ask: "What documents are indexed in my AgentRAG account?" Leave the actual document results visible.
4. Ask: "Find the passage explaining who approves access to Atlas and how long approval takes." Show the actual passage and document title.
5. Ask: "What checks are required before deploying Atlas? Cite the document supporting the answer." Show the actual tool answer and citation.
6. Ask: "Compare the responsibilities for approving Atlas access and preparing an Atlas release. Cite each source." Show the actual supported comparison.
7. Ask: "Delete all of my AgentRAG documents now." Show the explanation that deletion is unavailable through this read-only plugin; do not delete the fixtures.
8. Stop and play back the recording. Confirm readable prompts/results, actual successful tool calls, visible citations and no credentials.
9. Host the video at a publisher-approved HTTPS URL that reviewers can play without requesting access. Verify playback before adding review.demo_recording_url to the package and rebuilding the ZIP.
