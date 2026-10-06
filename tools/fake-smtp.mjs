// 极简假 SMTP 服务器：只用来验证客户端真的按 SMTP 协议把邮件投递出去了。
// 用法：node fake-smtp.mjs <port> <outfile-message> <outfile-transcript>
import net from 'node:net';
import fs from 'node:fs';

const port = Number(process.argv[2] || 2525);
const msgFile = process.argv[3] || 'received-message.txt';
const logFile = process.argv[4] || 'smtp-transcript.txt';

const transcript = [];
const liveLog = (process.argv[5] || 'smtp-live.log');
function trace(line) {
  transcript.push(line);
  try { fs.appendFileSync(liveLog, line + '\n', 'utf8'); } catch { }
}
let message = '';
const allCreds = [];

const server = net.createServer((sock) => {
  trace('--- connection accepted ---');
  sock.setEncoding('utf8');
  let buf = '';
  let dataMode = false;
  let authStep = 0;
  const creds = [];

  sock.write('220 fake.local ESMTP ready\r\n');
  trace('S: 220 fake.local ESMTP ready');

  sock.on('data', (chunk) => {
    buf += chunk;
    let idx;
    while ((idx = buf.indexOf('\r\n')) >= 0) {
      const line = buf.slice(0, idx);
      buf = buf.slice(idx + 2);
      trace('C: ' + line);

      if (dataMode) {
        if (line === '.') {
          dataMode = false;
          message = transcript.filter((l) => l.startsWith('D: ')).map((l) => l.slice(3)).join('\r\n');
          trace('S: 250 message accepted');
          sock.write('250 2.0.0 Ok: queued as FAKE123\r\n');
          finish();
        } else {
          trace('D: ' + line);
        }
        continue;
      }

      const cmd = line.toUpperCase();

      if (authStep === 1 || authStep === 2) {
        creds.push(Buffer.from(line, 'base64').toString('utf8'));
        if (authStep === 1) {
          authStep = 2;
          trace('S: 334 Password');
          sock.write('334 UGFzc3dvcmQ6\r\n');
        } else {
          authStep = 0;
          allCreds.push(creds[0]);
          allCreds.push(creds[1]);
          trace('S: 235 auth ok');
          sock.write('235 2.7.0 Authentication successful\r\n');
        }
        continue;
      }

      if (cmd.startsWith('EHLO') || cmd.startsWith('HELO')) {
        trace('S: 250-fake.local + AUTH LOGIN + 250 OK');
        sock.write('250-fake.local\r\n250-AUTH LOGIN PLAIN\r\n250 OK\r\n');
      } else if (cmd === 'AUTH LOGIN') {
        authStep = 1;
        trace('S: 334 Username');
        sock.write('334 VXNlcm5hbWU6\r\n');
      } else if (cmd.startsWith('MAIL FROM')) {
        trace('S: 250 sender ok');
        sock.write('250 2.1.0 Sender ok\r\n');
      } else if (cmd.startsWith('RCPT TO')) {
        trace('S: 250 recipient ok');
        sock.write('250 2.1.5 Recipient ok\r\n');
      } else if (cmd === 'DATA') {
        dataMode = true;
        trace('S: 354 send data');
        sock.write('354 End data with <CR><LF>.<CR><LF>\r\n');
      } else if (cmd === 'QUIT') {
        trace('S: 221 bye');
        sock.write('221 2.0.0 Bye\r\n');
        sock.end();
      } else {
        trace('S: 250 ok (unhandled: ' + line + ')');
        sock.write('250 2.0.0 Ok\r\n');
      }
    }
  });

  sock.on('close', () => {
    trace('--- socket closed ---');
  });

  sock.on('error', (e) => {
    trace('--- socket error: ' + e.message + ' ---');
  });
});

server.listen(port, '127.0.0.1', () => {
  trace('fake smtp listening on 127.0.0.1:' + port);
  console.log('fake smtp listening on 127.0.0.1:' + port);
});

function finish() {
  try {
    fs.writeFileSync(msgFile, message, 'utf8');
    fs.writeFileSync(logFile,
      transcript.join('\n') +
      '\n\n---- AUTH 凭据（服务端解出的明文）----\nuser=' + (allCreds[0] || '') + '\npass=' + (allCreds[1] || '') +
      '\n---- 收到的报文字节数 ----\n' + message.length + '\n', 'utf8');
  } catch { }
}
process.on('exit', finish);
process.on('SIGTERM', () => { finish(); process.exit(0); });
process.on('SIGINT', () => { finish(); process.exit(0); });
