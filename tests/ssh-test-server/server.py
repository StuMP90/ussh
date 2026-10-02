#!/usr/bin/env python3
"""Throwaway SSH server for ussh integration tests and manual testing (paramiko).

Gives each session a real login shell (bash) in a pty, so full-screen apps, resize and
`exit` behave like a normal server. Supports password and public-key auth, and local
(-L), dynamic (-D) and remote (-R) port forwarding.

    python3 server.py --port 2222 --host-key /tmp/hk --password secret \
        --authorized-key ~/.ssh/id_ed25519.pub

Never expose this on a network: it hands out a shell as the user running it.
"""
import argparse
import os
import pty
import select
import signal
import socket
import struct
import sys
import termios
import fcntl
import threading

import paramiko


def log(*args):
    print("[ssh-test-server]", *args, file=sys.stderr, flush=True)


class Server(paramiko.ServerInterface):
    def __init__(self, args, transport):
        self.args = args
        self.transport = transport
        self.pty_size = (80, 24)
        self.term = "xterm-256color"
        self.shell_requested = threading.Event()
        self.master_fd = None
        self.direct_tcpip = {}  # chanid -> upstream socket, claimed by the accept loop
        self.allowed_keys = []
        for path in args.authorized_key or []:
            with open(path) as f:
                for line in f:
                    parts = line.split()
                    if len(parts) >= 2:
                        self.allowed_keys.append((parts[0], parts[1]))

    # --- auth -----------------------------------------------------------
    def get_allowed_auths(self, username):
        methods = []
        if self.args.password is not None:
            methods += ["password", "keyboard-interactive"]
        if self.allowed_keys:
            methods.append("publickey")
        return ",".join(methods)

    def check_auth_password(self, username, password):
        if self.args.password is not None and password == self.args.password:
            return paramiko.AUTH_SUCCESSFUL
        return paramiko.AUTH_FAILED

    def check_auth_interactive(self, username, submethods):
        return paramiko.InteractiveQuery("", "", ("Password: ", False))

    def check_auth_interactive_response(self, responses):
        if self.args.password is not None and responses and responses[0] == self.args.password:
            return paramiko.AUTH_SUCCESSFUL
        return paramiko.AUTH_FAILED

    def check_auth_publickey(self, username, key):
        offered = (key.get_name(), key.get_base64())
        return paramiko.AUTH_SUCCESSFUL if offered in self.allowed_keys else paramiko.AUTH_FAILED

    # --- session --------------------------------------------------------
    def check_channel_request(self, kind, chanid):
        return paramiko.OPEN_SUCCEEDED if kind == "session" else paramiko.OPEN_FAILED_ADMINISTRATIVELY_PROHIBITED

    def check_channel_pty_request(self, channel, term, width, height, pixelwidth, pixelheight, modes):
        self.term = term.decode() if isinstance(term, bytes) else term
        self.pty_size = (width, height)
        return True

    def check_channel_shell_request(self, channel):
        self.shell_requested.set()
        return True

    def check_channel_window_change_request(self, channel, width, height, pixelwidth, pixelheight):
        self.pty_size = (width, height)
        if self.master_fd is not None:
            set_winsize(self.master_fd, height, width)
        return True

    # --- forwarding -----------------------------------------------------
    def check_channel_direct_tcpip_request(self, chanid, origin, destination):
        try:
            upstream = socket.create_connection(destination, timeout=5)
        except OSError as e:
            log("direct-tcpip to", destination, "failed:", e)
            return paramiko.OPEN_FAILED_CONNECT_FAILED
        upstream.settimeout(None)
        self.direct_tcpip[chanid] = upstream
        return paramiko.OPEN_SUCCEEDED

    def check_port_forward_request(self, address, port):
        listener = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        listener.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        listener.bind((address or "127.0.0.1", port))
        listener.listen(8)
        bound = listener.getsockname()[1]

        def accept_loop():
            while self.transport.is_active():
                try:
                    client, peer = listener.accept()
                except OSError:
                    return
                try:
                    channel = self.transport.open_forwarded_tcpip_channel(peer, (address, bound))
                except Exception as e:
                    log("forwarded-tcpip failed:", e)
                    client.close()
                    continue
                threading.Thread(target=pipe, args=(channel, client), daemon=True).start()

        threading.Thread(target=accept_loop, daemon=True).start()
        log("remote forward listening on", bound)
        return bound

    def cancel_port_forward_request(self, address, port):
        pass


def set_winsize(fd, rows, cols):
    fcntl.ioctl(fd, termios.TIOCSWINSZ, struct.pack("HHHH", rows, cols, 0, 0))


def pipe(channel, sock):
    try:
        while True:
            r, _, _ = select.select([channel, sock], [], [], 1.0)
            if channel in r:
                data = channel.recv(32768)
                if not data:
                    break
                sock.sendall(data)
            if sock in r:
                data = sock.recv(32768)
                if not data:
                    break
                channel.sendall(data)
            if channel.closed:
                break
    except Exception:
        pass
    finally:
        channel.close()
        sock.close()


def run_shell(channel, server):
    pid, master_fd = pty.fork()
    if pid == 0:
        env = {
            "TERM": server.term,
            "HOME": os.environ.get("HOME", "/tmp"),
            "PATH": os.environ.get("PATH", "/usr/bin:/bin"),
            "LANG": "C.UTF-8",
            "PS1": r"ussh-test$ ",
        }
        os.execve("/bin/bash", ["bash", "--norc", "--noprofile", "-i"], env)
    server.master_fd = master_fd
    set_winsize(master_fd, server.pty_size[1], server.pty_size[0])
    try:
        while True:
            r, _, _ = select.select([channel, master_fd], [], [], 1.0)
            if channel in r:
                data = channel.recv(32768)
                if not data:
                    break
                os.write(master_fd, data)
            if master_fd in r:
                try:
                    data = os.read(master_fd, 32768)
                except OSError:
                    data = b""
                if not data:
                    break
                channel.sendall(data)
            if channel.closed:
                break
    finally:
        try:
            os.kill(pid, signal.SIGHUP)
        except ProcessLookupError:
            pass
        try:
            _, status = os.waitpid(pid, 0)
            channel.send_exit_status(os.waitstatus_to_exitcode(status))
        except Exception:
            pass
        os.close(master_fd)
        channel.close()


def handle(client, args, host_key):
    transport = paramiko.Transport(client)
    transport.add_server_key(host_key)
    server = Server(args, transport)
    try:
        transport.start_server(server=server)
    except Exception as e:
        log("negotiation failed:", e)
        return
    # One accept loop for every channel: forwarded (direct-tcpip) channels get piped, the
    # session channel gets a shell. A bastion may only ever open forwarded channels.
    def session(channel):
        if server.shell_requested.wait(10):
            run_shell(channel, server)
        transport.close()

    while transport.is_active():
        channel = transport.accept(1)
        if channel is None:
            continue
        upstream = server.direct_tcpip.pop(channel.get_id(), None)
        if upstream is not None:
            threading.Thread(target=pipe, args=(channel, upstream), daemon=True).start()
        else:
            threading.Thread(target=session, args=(channel,), daemon=True).start()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--port", type=int, default=2222)
    parser.add_argument("--bind", default="127.0.0.1")
    parser.add_argument("--host-key", required=True, help="RSA host key file (created if missing)")
    parser.add_argument("--password")
    parser.add_argument("--authorized-key", action="append")
    args = parser.parse_args()

    if os.path.exists(args.host_key):
        host_key = paramiko.RSAKey(filename=args.host_key)
    else:
        host_key = paramiko.RSAKey.generate(2048)
        host_key.write_private_key_file(args.host_key)

    listener = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    listener.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    listener.bind((args.bind, args.port))
    listener.listen(16)
    log(f"listening on {args.bind}:{args.port}")
    print("READY", flush=True)
    while True:
        client, _ = listener.accept()
        threading.Thread(target=handle, args=(client, args, host_key), daemon=True).start()


if __name__ == "__main__":
    main()
