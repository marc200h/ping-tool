import tkinter as tk
from tkinter import ttk, scrolledtext
import subprocess
import threading
import re
import os
from datetime import datetime


class PingApp:
    def __init__(self, root):
        self.root = root
        self.root.title("Ping Tool")
        self.root.geometry("600x520")
        self.root.resizable(True, True)
        self.root.configure(bg="#1e1e2e")

        self.ping_thread = None
        self.stop_flag = False

        self._build_ui()

    def _build_ui(self):
        style = ttk.Style()
        style.theme_use("clam")
        style.configure("TLabel", background="#1e1e2e", foreground="#cdd6f4", font=("Segoe UI", 10))
        style.configure("TEntry", fieldbackground="#313244", foreground="#cdd6f4", insertcolor="#cdd6f4")
        style.configure("TButton", background="#89b4fa", foreground="#1e1e2e", font=("Segoe UI", 10, "bold"), padding=6)
        style.map("TButton", background=[("active", "#74c7ec")])
        style.configure("Stop.TButton", background="#f38ba8", foreground="#1e1e2e", font=("Segoe UI", 10, "bold"), padding=6)
        style.map("Stop.TButton", background=[("active", "#eba0ac")])
        style.configure("TCombobox", fieldbackground="#313244", foreground="#cdd6f4", selectbackground="#45475a")

        # Title
        title = tk.Label(self.root, text="Ping Tool", bg="#1e1e2e", fg="#89b4fa",
                         font=("Segoe UI", 16, "bold"))
        title.pack(pady=(16, 4))

        # Input frame
        input_frame = tk.Frame(self.root, bg="#1e1e2e")
        input_frame.pack(fill=tk.X, padx=20, pady=6)

        ttk.Label(input_frame, text="IPv4 Address:").grid(row=0, column=0, sticky="w", padx=(0, 8))
        self.ip_entry = ttk.Entry(input_frame, width=24, font=("Segoe UI", 11))
        self.ip_entry.grid(row=0, column=1, padx=(0, 16))
        self.ip_entry.bind("<Return>", lambda e: self._start_ping())

        ttk.Label(input_frame, text="Count:").grid(row=0, column=2, sticky="w", padx=(0, 8))
        self.count_var = tk.StringVar(value="4")
        count_combo = ttk.Combobox(input_frame, textvariable=self.count_var, width=6,
                                   values=["4", "10", "20", "50", "100", "Continuous"],
                                   state="readonly")
        count_combo.grid(row=0, column=3, padx=(0, 16))

        ttk.Label(input_frame, text="Timeout (ms):").grid(row=0, column=4, sticky="w", padx=(0, 8))
        self.timeout_var = tk.StringVar(value="1000")
        timeout_entry = ttk.Entry(input_frame, textvariable=self.timeout_var, width=7,
                                  font=("Segoe UI", 10))
        timeout_entry.grid(row=0, column=5)

        ttk.Label(input_frame, text="Size (bytes):").grid(row=1, column=0, sticky="w", padx=(0, 8), pady=(6, 0))
        self.size_var = tk.StringVar(value="32")
        size_entry = ttk.Entry(input_frame, textvariable=self.size_var, width=8,
                               font=("Segoe UI", 10))
        size_entry.grid(row=1, column=1, padx=(0, 16), pady=(6, 0), sticky="w")
        ttk.Label(input_frame, text="(32 – 65535)").grid(row=1, column=2, sticky="w", pady=(6, 0))
        self.no_frag_var = tk.BooleanVar(value=False)
        ttk.Checkbutton(input_frame, text="Don't Fragment (-f)", variable=self.no_frag_var).grid(
            row=1, column=3, sticky="w", padx=(24, 0), pady=(6, 0))

        # Buttons
        btn_frame = tk.Frame(self.root, bg="#1e1e2e")
        btn_frame.pack(pady=8)

        self.ping_btn = ttk.Button(btn_frame, text="Ping", command=self._start_ping)
        self.ping_btn.pack(side=tk.LEFT, padx=6)

        self.stop_btn = ttk.Button(btn_frame, text="Stop", command=self._stop_ping,
                                   style="Stop.TButton", state=tk.DISABLED)
        self.stop_btn.pack(side=tk.LEFT, padx=6)

        clear_btn = ttk.Button(btn_frame, text="Clear", command=self._clear)
        clear_btn.pack(side=tk.LEFT, padx=6)

        # Status bar
        self.status_var = tk.StringVar(value="Ready")
        status_label = tk.Label(self.root, textvariable=self.status_var, bg="#181825",
                                fg="#a6adc8", font=("Segoe UI", 9), anchor="w", padx=10)
        status_label.pack(fill=tk.X, padx=20, pady=(0, 4))

        # Output area
        output_frame = tk.Frame(self.root, bg="#1e1e2e")
        output_frame.pack(fill=tk.BOTH, expand=True, padx=20, pady=(0, 8))

        self.output = scrolledtext.ScrolledText(
            output_frame,
            bg="#11111b", fg="#cdd6f4",
            font=("Consolas", 10),
            insertbackground="#cdd6f4",
            selectbackground="#45475a",
            relief=tk.FLAT,
            borderwidth=8,
            state=tk.DISABLED
        )
        self.output.pack(fill=tk.BOTH, expand=True)
        self.output.tag_config("success", foreground="#a6e3a1")
        self.output.tag_config("failure", foreground="#f38ba8")
        self.output.tag_config("header",  foreground="#89b4fa")
        self.output.tag_config("stats",   foreground="#fab387")
        self.output.tag_config("info",    foreground="#a6adc8")

        # Stats bar
        self.stats_var = tk.StringVar(value="")
        stats_label = tk.Label(self.root, textvariable=self.stats_var, bg="#181825",
                               fg="#fab387", font=("Segoe UI", 9), anchor="w", padx=10)
        stats_label.pack(fill=tk.X, padx=20, pady=(0, 10))

    _log_lock = threading.Lock()

    def _log_missed_ping(self, ip):
        now = datetime.now()
        log_dir = os.path.dirname(os.path.abspath(__file__))
        log_path = os.path.join(log_dir, f"missed_pings_{now.strftime('%Y-%m-%d')}.csv")
        with self._log_lock:
            write_header = not os.path.exists(log_path)
            with open(log_path, "a", newline="") as f:
                if write_header:
                    f.write("Timestamp,IP Address\n")
                f.write(f"{now.strftime('%Y-%m-%d %H:%M:%S')},{ip}\n")

    def _write(self, text, tag=None):
        self.output.configure(state=tk.NORMAL)
        if tag:
            self.output.insert(tk.END, text, tag)
        else:
            self.output.insert(tk.END, text)
        self.output.see(tk.END)
        self.output.configure(state=tk.DISABLED)

    def _clear(self):
        self.output.configure(state=tk.NORMAL)
        self.output.delete("1.0", tk.END)
        self.output.configure(state=tk.DISABLED)
        self.stats_var.set("")
        self.status_var.set("Ready")

    def _validate_ip(self, ip):
        pattern = r"^(\d{1,3}\.){3}\d{1,3}$"
        if not re.match(pattern, ip):
            return False
        return all(0 <= int(o) <= 255 for o in ip.split("."))

    def _start_ping(self):
        ip = self.ip_entry.get().strip()
        if not ip:
            self.status_var.set("Please enter an IPv4 address.")
            return
        if not self._validate_ip(ip):
            self.status_var.set(f"Invalid IPv4 address: {ip}")
            return

        count_val = self.count_var.get()
        continuous = count_val == "Continuous"
        count = 0 if continuous else int(count_val)

        try:
            timeout = int(self.timeout_var.get())
        except ValueError:
            self.status_var.set("Invalid timeout value.")
            return

        try:
            size = int(self.size_var.get())
            if not (32 <= size <= 65535):
                raise ValueError
        except ValueError:
            self.status_var.set("Size must be between 32 and 65535 bytes.")
            return

        self.stop_flag = False
        self.ping_btn.configure(state=tk.DISABLED)
        self.stop_btn.configure(state=tk.NORMAL)

        no_frag = self.no_frag_var.get()
        self.ping_thread = threading.Thread(
            target=self._run_ping, args=(ip, count, continuous, timeout, size, no_frag), daemon=True
        )
        self.ping_thread.start()

    def _stop_ping(self):
        self.stop_flag = True
        self.status_var.set("Stopping...")

    def _run_ping(self, ip, count, continuous, timeout_ms, size, no_frag):
        sent = 0
        received = 0
        rtts = []

        timestamp = datetime.now().strftime("%Y-%m-%d %H:%M:%S")
        header = f"\n[{timestamp}] Pinging {ip}"
        header += f" (continuous)" if continuous else f" × {count}"
        header += f"  size={size}B  timeout={timeout_ms}ms"
        if no_frag:
            header += "  DF=on"
        header += "\n"
        self.root.after(0, self._write, header, "header")

        seq = 0
        while not self.stop_flag:
            seq += 1
            cmd = ["ping", "-n", "1", "-w", str(timeout_ms), "-l", str(size), ip]
            if no_frag:
                cmd.insert(-1, "-f")
            try:
                result = subprocess.run(cmd, capture_output=True, text=True, timeout=timeout_ms / 1000 + 2)
                output = result.stdout
            except subprocess.TimeoutExpired:
                output = ""

            sent += 1
            rtt_match = re.search(r"time[=<](\d+)ms", output)
            ttl_match = re.search(r"TTL=(\d+)", output, re.IGNORECASE)

            if rtt_match:
                rtt = int(rtt_match.group(1))
                ttl = ttl_match.group(1) if ttl_match else "?"
                rtts.append(rtt)
                received += 1
                line = f"  Reply from {ip}: seq={seq}  time={rtt}ms  TTL={ttl}\n"
                self.root.after(0, self._write, line, "success")
            else:
                line = f"  Request timeout  seq={seq}\n"
                self.root.after(0, self._write, line, "failure")
                self._log_missed_ping(ip)

            loss = round((sent - received) / sent * 100) if sent else 0
            avg_rtt = round(sum(rtts) / len(rtts)) if rtts else 0
            stats = f"Sent: {sent}   Received: {received}   Loss: {loss}%   Avg RTT: {avg_rtt}ms"
            self.root.after(0, self.stats_var.set, stats)
            self.root.after(0, self.status_var.set, f"Pinging {ip}...")

            if not continuous and seq >= count:
                break

        # Summary
        loss = round((sent - received) / sent * 100) if sent else 0
        min_rtt = min(rtts) if rtts else 0
        max_rtt = max(rtts) if rtts else 0
        avg_rtt = round(sum(rtts) / len(rtts)) if rtts else 0
        summary = (
            f"\n  --- Ping statistics for {ip} ---\n"
            f"  Packets: Sent={sent}, Received={received}, Lost={sent - received} ({loss}% loss)\n"
        )
        if rtts:
            summary += f"  RTT: min={min_rtt}ms  avg={avg_rtt}ms  max={max_rtt}ms\n"
        self.root.after(0, self._write, summary, "stats")
        self.root.after(0, self.status_var.set, f"Done — {ip}")
        self.root.after(0, self.ping_btn.configure, {"state": tk.NORMAL})
        self.root.after(0, self.stop_btn.configure, {"state": tk.DISABLED})


if __name__ == "__main__":
    root = tk.Tk()
    app = PingApp(root)
    root.mainloop()
