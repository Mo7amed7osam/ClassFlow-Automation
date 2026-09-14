import assert from "node:assert/strict";
import test from "node:test";
import { classifyLink, driveFileIdOf, previewLink } from "../src/recording-links.mjs";

const DRIVE = "https://drive.google.com/file/d/1A1bzBcdEfGhIjKlMnOpQrStUv/view?usp=sharing";

test("only Zoom share links and single Drive files are attachable", () => {
  assert.equal(classifyLink(DRIVE), "googleDrive");
  assert.equal(classifyLink("https://drive.google.com/open?id=1A1bzBcdEfGhIjKlMnOpQrStUv"), "googleDrive");
  assert.equal(classifyLink("https://us06web.zoom.us/rec/share/abc?startTime=1725000000000"), "zoomShare");
  assert.equal(classifyLink("https://drive.google.com/drive/folders/1A1bzBcdEfGhIjKlMnOpQrStUv"), "none");
  assert.equal(classifyLink("http://drive.google.com/file/d/1A1bzBcdEfGhIjKlMnOpQrStUv/view"), "none");
  assert.equal(classifyLink("https://drive.google.com:444/file/d/1A1bzBcdEfGhIjKlMnOpQrStUv/view"), "none");
  assert.equal(classifyLink("https://drive.google.com/uc?id=1A1bzBcdEfGhIjKlMnOpQrStUv"), "none");
  assert.equal(driveFileIdOf(`${DRIVE} `), null);
  assert.equal(previewLink(DRIVE), "drive.google.com/file/d/1A1bzB...");
});

