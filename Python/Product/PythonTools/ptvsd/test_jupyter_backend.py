import logging
import threading
import time
from repl.jupyter_client import JupyterClientBackend

# Set up logging for the test script
logging.basicConfig(level=logging.DEBUG,
                    format='%(asctime)s [%(levelname)s] %(threadName)s: %(message)s',
                    handlers=[logging.StreamHandler()])

def test_backend():
    logging.debug('Creating JupyterClientBackend instance')
    backend = JupyterClientBackend()
    
    logging.debug('Starting execution loop in a separate thread')
    execution_thread = threading.Thread(target=backend.execution_loop, name='ExecutionLoopThread')
    execution_thread.start()
    
    # Wait a bit to ensure the backend has started
    time.sleep(2)
    
    logging.debug('Running a test command')
    backend.run_command('print("Hello from IPython backend")')
    
    # Wait for the command to be processed
    time.sleep(2)
    
    logging.debug('Requesting backend to exit')
    backend.exit_process()
    
    logging.debug('Waiting for execution thread to finish')
    execution_thread.join()
    logging.debug('Execution thread has finished')

if __name__ == '__main__':
    test_backend()
