# Python Tools for Visual Studio
# Copyright(c) Microsoft Corporation
# All rights reserved.
# 
# Licensed under the Apache License, Version 2.0 (the License); you may not use
# this file except in compliance with the License. You may obtain a copy of the
# License at http://www.apache.org/licenses/LICENSE-2.0
# 
# THIS CODE IS PROVIDED ON AN  *AS IS* BASIS, WITHOUT WARRANTIES OR CONDITIONS
# OF ANY KIND, EITHER EXPRESS OR IMPLIED, INCLUDING WITHOUT LIMITATION ANY
# IMPLIED WARRANTIES OR CONDITIONS OF TITLE, FITNESS FOR A PARTICULAR PURPOSE,
# MERCHANTABILITY OR NON-INFRINGEMENT.
# 
# See the Apache Version 2.0 License for specific language governing
# permissions and limitations under the License.

"""
PTVS REPL host process. 
"""

__author__ = "Microsoft Corporation <ptvshelp@microsoft.com>"
__version__ = "3.1.0.0"

import os
import os.path
import socket
import sys
import threading
import traceback

def _get_repl():
    try:
        ptvs_lib_path = os.path.dirname(__file__)
        sys.path.insert(0, ptvs_lib_path)
        import ptvsd.repl
    except:
        traceback.print_exc()
        print('''
    Internal error detected. Please copy the above traceback and report at
    https://go.microsoft.com/fwlink/?LinkId=293415

    Press Enter to close. . .''')
        try:
            raw_input()
        except NameError:
            input()
        sys.exit(1)
    finally:
        sys.path.remove(ptvs_lib_path)
    return ptvsd.repl

# Arguments are:
# 1. Working directory.
# 2. VS debugger port to connect to.
# 3. GUID for the debug session.
# 4. Debug options (as list of names - see enum PythonDebugOptions).
# 5. '-m' or '-c' to override the default run-as mode. [optional]
# 6. Startup script name.
# 7. Script arguments.

def _run_repl():
    from optparse import OptionParser
    repl = _get_repl()

    parser = OptionParser(prog='repl', description='Process REPL options')
    parser.add_option('--port', dest='port',
                      help='the port to connect back to')
    parser.add_option('--execution-mode', dest='backend',
                      help='the backend to use')
    parser.add_option('--enable-attach', dest='enable_attach', 
                      action="store_true", default=False,
                      help='enable attaching the debugger via $attach')

    (options, args) = parser.parse_args()

    backend_type = repl.BasicReplBackend
    backend_error = None
    if options.backend is not None and options.backend.lower() != 'standard':
        try:
            split_backend = options.backend.split('.')
            backend_mod_name = '.'.join(split_backend[:-1])
            backend_name = split_backend[-1]
            backend_type = getattr(__import__(backend_mod_name, fromlist=['*']), backend_name)
        except repl.UnsupportedReplException:
            backend_error = sys.exc_info()[1].reason
        except:
            backend_error = traceback.format_exc()

    # fix sys.path so that cwd is where the project lives.
    sys.path[0] = '.'
    # remove all of our parsed args in case we have a launch file that cares...
    sys.argv = args or ['']

    try:
        backend = backend_type()
    except repl.UnsupportedReplException:
        backend_error = sys.exc_info()[1].reason
        backend = repl.BasicReplBackend()
    except Exception:
        backend_error = traceback.format_exc()
        backend = repl.BasicReplBackend()

    repl.BACKEND = backend
    backend.connect(int(options.port))

    if options.enable_attach:
        backend.init_debugger()


    if backend_error is not None:
        sys.stderr.write('Error using selected REPL back-end:\n')
        sys.stderr.write(backend_error + '\n')
        sys.stderr.write('Using standard backend instead\n')

    # execute code on the main thread which we can interrupt
    backend.execution_loop()    


def mock_server(host="127.0.0.1", port=57905):
    server_socket = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    server_socket.bind((host, port))
    server_socket.listen(1)
    print(f"Mock server running at {host}:{port}")

    conn, addr = server_socket.accept()
    print(f"Connected by {addr}")
    while True:
        data = conn.recv(1024)
        if not data:
            break
        print(f"Received: {data.decode('utf-8')}")
        conn.sendall(b"OK")  # Echo response
    conn.close()
    server_socket.close()



if __name__ == '__main__':
    # Start the mock server in a separate thread
    threading.Thread(target=mock_server, daemon=True).start()
    if getattr(_get_repl(), 'DEBUG', False):
        try:
            _run_repl()
        except:
            sys.__stdout__.write(traceback.format_exc())
            sys.__stdout__.write('\n\nPress Enter to close...')
            sys.__stdout__.flush()
            try:
                raw_input()
            except NameError:
                input()
            raise
    else:
        _run_repl()
