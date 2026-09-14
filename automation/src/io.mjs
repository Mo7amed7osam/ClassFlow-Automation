// The helper talks to the app over stdout, one JSON object per line.
//
//   {"type":"log","level":"info","message":"..."}     progress, shown in the app's log
//   {"type":"event","name":"admitted","data":{...}}    for long-running commands
//   {"type":"result","success":true,...}               exactly once, last
//
// Nothing else may be written to stdout: a stray console.log would be a line the
// app cannot parse. Playwright's own noise goes to stderr.

function write(object) {
  process.stdout.write(`${JSON.stringify(object)}\n`);
}

export const log = {
  info: (message) => write({ type: "log", level: "info", message }),
  warn: (message) => write({ type: "log", level: "warn", message }),
  error: (message) => write({ type: "log", level: "error", message }),
  success: (message) => write({ type: "log", level: "success", message }),
};

export function emit(name, data = {}) {
  write({ type: "event", name, data });
}

export function result(body) {
  write({ type: "result", ...body });
}

/** Reads the whole request from stdin. An empty stdin is an empty request. */
export async function readRequest(stream = process.stdin) {
  if (stream.isTTY) return {};
  const chunks = [];
  for await (const chunk of stream) chunks.push(chunk);
  const text = Buffer.concat(chunks).toString("utf8").trim();
  if (text.length === 0) return {};
  const parsed = JSON.parse(text);
  if (parsed === null || typeof parsed !== "object" || Array.isArray(parsed)) {
    throw new Error("The request must be a JSON object.");
  }
  return parsed;
}

/** Reads the first line of stdin, leaving the stream open for a later "stop". */
export function readFirstLine(stream = process.stdin) {
  return new Promise((resolve, reject) => {
    let buffer = "";
    const onData = (chunk) => {
      buffer += chunk.toString("utf8");
      const newline = buffer.indexOf("\n");
      if (newline < 0) return;
      stream.off("data", onData);
      stream.off("end", onEnd);
      try {
        resolve(JSON.parse(buffer.slice(0, newline)));
      } catch (error) {
        reject(error);
      }
    };
    const onEnd = () => {
      stream.off("data", onData);
      try {
        resolve(buffer.trim() ? JSON.parse(buffer) : {});
      } catch (error) {
        reject(error);
      }
    };
    stream.on("data", onData);
    stream.on("end", onEnd);
  });
}
