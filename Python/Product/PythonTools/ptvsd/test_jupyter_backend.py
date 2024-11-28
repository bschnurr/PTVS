import logging
import socket
import threading
import time
from repl.ipython_client import IPythonBackend
from repl.jupyter_client import JupyterClientBackend

# Set up logging for the test script
logging.basicConfig(level=logging.DEBUG,
                    format='%(asctime)s [%(levelname)s] %(threadName)s: %(message)s',
                    handlers=[logging.StreamHandler()])


def mock_server(host="127.0.0.1", port=9999):
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


def test_backend():
    
    # Start the mock server in a separate thread
    threading.Thread(target=mock_server, daemon=True).start()

    logging.debug('Creating JupyterClientBackend instance')
    backend = IPythonBackend() #JupyterClientBackend()
    backend.connect(port=9999)
    
    logging.debug('Starting execution loop in a separate thread')
    execution_thread = threading.Thread(target=backend.execution_loop, name='ExecutionLoopThread')
    execution_thread.start()
    
    time.sleep(8)
    # Wait a bit to ensure the backend has started
    # backend.wait_for_kernel_ready()

    while True:
        time.sleep(2)
        print('Running a test command')
    
    logging.debug('Running a test command')
    backend.run_command('print("Hello from IPython backend")')
    
    # Wait for the command to be processed
    time.sleep(2)
    
    logging.debug('Requesting backend to exit')
    backend.exit_process()
    
    logging.debug('Waiting for execution thread to finish')
    execution_thread.join()
    logging.debug('Execution thread has finished')


def wait_for_kernel_ready(kc, timeout=10):
    start_time = time.time()
    while True:
        try:
            kc.kernel_info()
            print("Kernel is ready.")
            break
        except RuntimeError as e:
            if time.time() - start_time > timeout:
                raise TimeoutError("Kernel did not become ready in time.") from e
            print("Waiting for kernel to be ready...")
            time.sleep(0.5)  # Wait a bit before retrying
if __name__ == '__main__':
    test_backend()
