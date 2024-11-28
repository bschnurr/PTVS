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

"""Implements REPL support over IPython/ZMQ for VisualStudio"""

__author__ = "Microsoft Corporation <ptvshelp@microsoft.com>"
__version__ = "3.2.1.0"

import os
import re
import sys
import typing as t

from jupyter_client.blocking.client import BlockingKernelClient
from jupyter_client.client import KernelClient
from ptvsd.repl import BasicReplBackend, ReplBackend, UnsupportedReplException, _command_line_to_args_list, unicode
from jupyter_client.manager import KernelManager
from jupyter_client.channels import HBChannel
from jupyter_client.threaded import ThreadedZMQSocketChannel


from traitlets import Type


from ptvsd.util import to_bytes

import threading

from base64 import decodebytes as decodestring # Deprecated in 3.9

try:
    import IPython
except ImportError:
    exc_value = sys.exc_info()[1]
    raise UnsupportedReplException('IPython mode requires IPython 0.11 or later: ' + str(exc_value))

def is_ipython_versionorgreater(major, minor):
    """checks if we are at least a specific IPython version"""
    match = re.match('(\d+).(\d+)', IPython.__version__)
    if match:
        groups = match.groups()
        if int(groups[0]) > major:
            return True
        elif int(groups[0]) == major:
            return int(groups[1]) >= minor

    return False

remove_escapes = re.compile(r'\x1b[^m]*m')


try:
    # For Jupyter Client >= 6.0, the API is unified under `jupyter_client`
    ShellChannel =  ThreadedZMQSocketChannel
    IOPubChannel = ThreadedZMQSocketChannel
    StdInChannel = ThreadedZMQSocketChannel
    HBChannel = HBChannel
    

    from traitlets import Type
except ImportError as e:
    raise RuntimeError("Jupyter Client or required channels are not installed: " + str(e))


# TODO: SystemExit exceptions come back to us as strings, can we automatically exit when ones raised somehow?

#####
# Channels which forward events

# Description of the messaging protocol
# http://ipython.scipy.org/doc/manual/html/development/messaging.html 


class DefaultHandler(object):
    def unknown_command(self, content): 
        import pprint
        print('unknown command ' + str(type(self)))
        pprint.pprint(content)

    def call_handlers(self, msg):
        # msg_type:
        #   execute_reply
        msg_type = 'handle_' + msg['msg_type']
        
        getattr(self, msg_type, self.unknown_command)(msg['content'])
    
class VsShellChannel(DefaultHandler, ShellChannel):
    
    def handle_execute_reply(self, content):
        # we could have a payload here...
        payload = content['payload']
        
        for item in payload:
            data = item.get('data')
            if data is not None:
                try:
                    # Could be named km.sub_channel for very old IPython, but
                    # those versions should not put 'data' in this payload
                    write_data = self._vs_backend.km.iopub_channel.write_data
                except AttributeError:
                    pass
                else:
                    write_data(data)
                    continue

            output = item.get('text', None)
            if output is not None:
                self._vs_backend.write_stdout(output)
        self._vs_backend.send_command_executed()
        
    def handle_inspect_reply(self, content):
        self.handle_object_info_reply(content)

    def handle_object_info_reply(self, content):
        self._vs_backend.object_info_reply = content
        self._vs_backend.members_lock.release()

    def handle_complete_reply(self, content):
        self._vs_backend.complete_reply = content
        self._vs_backend.members_lock.release()

    def handle_kernel_info_reply(self, content):
        self._vs_backend.write_stdout(content['banner'])


class VsIOPubChannel(DefaultHandler, IOPubChannel):
    def call_handlers(self, msg):
        # only output events from our session or no sessions
        # https://pytools.codeplex.com/workitem/1622
        parent = msg.get('parent_header')
        if not parent or parent.get('session') == self.session.session:
            msg_type = 'handle_' + msg['msg_type']
            getattr(self, msg_type, self.unknown_command)(msg['content'])
        
    def handle_display_data(self, content):
        # called when user calls display()
        data = content.get('data', None)
        
        if data is not None:
            self.write_data(data)
    
    def handle_stream(self, content):
        stream_name = content['name']
        if is_ipython_versionorgreater(3, 0):
            output = content['text']
        else:
            output = content['data']
        if stream_name == 'stdout':
            self._vs_backend.write_stdout(output)
        elif stream_name == 'stderr':
            self._vs_backend.write_stderr(output)
        # TODO: stdin can show up here, do we echo that?
    
    def handle_execute_result(self, content):
        self.handle_execute_output(content)

    def handle_execute_output(self, content):
        # called when an expression statement is printed, we treat 
        # identical to stream output but it always goes to stdout
        output = content['data']
        execution_count = content['execution_count']
        self._vs_backend.execution_count = execution_count + 1
        self._vs_backend.send_prompt(
            '\r\nIn [%d]: ' % (execution_count + 1),
            '   ' + ('.' * (len(str(execution_count + 1)) + 2)) + ': ',
            allow_multiple_statements=True
        )
        self.write_data(output, execution_count)
        
    def write_data(self, data, execution_count = None):
        output_xaml = data.get('application/xaml+xml', None)
        if output_xaml is not None:
            try:
                if isinstance(output_xaml, str) and sys.version_info[0] >= 3:
                    output_xaml = output_xaml.encode('ascii')
                self._vs_backend.write_xaml(str(decodestring(output_xaml)))
                self._vs_backend.write_stdout('\n') 
                return
            except:
                pass
        
        output_png = data.get('image/png', None)
        if output_png is not None:
            try:
                if isinstance(output_png, str) and sys.version_info[0] >= 3:
                    output_png = output_png.encode('ascii')
                self._vs_backend.write_png(str(decodestring(output_png)))
                self._vs_backend.write_stdout('\n') 
                return
            except:
                pass
            
        output_str = data.get('text/plain', None)
        if output_str is not None:
            if execution_count is not None:
                if '\n' in output_str:
                    output_str = '\n' + output_str
                output_str = 'Out[' + str(execution_count) + ']: ' + output_str

            self._vs_backend.write_stdout(output_str)
            self._vs_backend.write_stdout('\n') 
            return

    def handle_error(self, content):
        # TODO: this includes escape sequences w/ color, we need to unescape that
        ename = content['ename']
        evalue = content['evalue']
        tb = content['traceback']
        self._vs_backend.write_stderr('\n'.join(tb))
        self._vs_backend.write_stdout('\n')
    
    def handle_execute_input(self, content):
        # just a rebroadcast of the command to be executed, can be ignored
        self._vs_backend.execution_count += 1
        self._vs_backend.send_prompt(
            '\r\nIn [%d]: ' % (self._vs_backend.execution_count),
            '   ' + ('.' * (len(str(self._vs_backend.execution_count)) + 2)) + ': ',
            allow_multiple_statements=True
        )
        pass
        
    def handle_status(self, content):
        pass

    # Backwards compat w/ 0.13
    handle_pyin = handle_execute_input
    handle_pyout = handle_execute_output
    handle_pyerr = handle_error


class VsStdInChannel(DefaultHandler, StdInChannel):
    def input(self, string):
        """Send an input_reply message back to the kernel."""
        msg = self.session.msg('input_reply', content={'value': string})
        self.session.send(self.socket, msg)
        
    def handle_input_request(self, content):
        # queue this to another thread so we don't block the channel
        def read_and_respond():
            value = self._vs_backend.read_line()
        
            self.input(value)
            
        thread = threading.Thread(target=read_and_respond)
        thread.start()


class VsHBChannel(DefaultHandler, HBChannel):
    if is_ipython_versionorgreater(3, 0):
        def call_handlers(self, time_since_last_hb):
            pass



class VsKernelManager(KernelManager):
    shell_channel_class = VsShellChannel
    iopub_channel_class = VsIOPubChannel
    stdin_channel_class = VsStdInChannel
    hb_channel_class = VsHBChannel

class VsKernelClient(KernelClient):
    shell_channel_class = VsShellChannel
    iopub_channel_class = VsIOPubChannel
    stdin_channel_class = VsStdInChannel
    hb_channel_class = VsHBChannel
   


class IPythonBackend(ReplBackend):
    def __init__(self, mod_name = '__main__', launch_file = None):
        ReplBackend.__init__(self)
        self.launch_file = launch_file
        self.mod_name = mod_name
        self.km = VsKernelManager()
        self.km.start_kernel()
        self.kc: BlockingKernelClient = self.km.client()
     
        
        # if is_ipython_versionorgreater(0, 13):
        #     # http://pytools.codeplex.com/workitem/759
        #     # IPython stopped accepting the ipython flag and switched to launcher, the new
        #     # default is what we want though.
        #     self.km.start_kernel(**{'extra_arguments': self.get_extra_arguments()})
        # else:
        #     self.km.start_kernel(**{'ipython': True, 'extra_arguments': self.get_extra_arguments()})
        
        try:
            self.kc.start_channels()
        except Exception as e:
            print(f"Error start_channels: {e}")

        

        self.exit_lock = threading.Lock()
        self.exit_lock.acquire()     # used as an event
        self.members_lock = threading.Lock()
        self.members_lock.acquire()
        
        self.kc.shell_channel._vs_backend = self
        self.kc.stdin_channel._vs_backend = self
        if is_ipython_versionorgreater(1, 0):
            self.kc.iopub_channel._vs_backend = self
        else:
            self.kc.sub_channel._vs_backend = self
        self.kc.hb_channel._vs_backend = self
        self.execution_count = 1

        try:
            self.kc.wait_for_ready(timeout=10)  # Wait up to 10 seconds
            print("Kernel is ready.")
        except RuntimeError:
            print("Kernel did not become ready within the timeout.")

    def get_extra_arguments(self):
        if sys.version <= '2.':
            return [unicode('--pylab=inline')]
        return ['--IPKernelApp.matplotlib=inline']
        
    def execute_file_as_main(self, filename, arg_string):
        f = open(filename, 'rb')
        try:
            contents = f.read().replace(to_bytes("\r\n"), to_bytes("\n"))
        finally:
            f.close()
        args = [filename] + _command_line_to_args_list(arg_string)
        code = '''
import sys
sys.argv = %(args)r
__file__ = %(filename)r
del sys
exec(compile(%(contents)r, %(filename)r, 'exec')) 
''' % {'filename' : filename, 'contents':contents, 'args': args}
        
        self.run_command(code, True)

    def execution_loop(self):
        # we've got a bunch of threads setup for communication, we just block
        # here until we're requested to exit.  
        self.send_prompt('\r\nIn [1]: ', '   ...: ', allow_multiple_statements=True)
        self.exit_lock.acquire()
    
    def run_command(self, command, silent = False):
        if is_ipython_versionorgreater(3, 0):
            self.kc.execute(command, silent)
        else:
            self.kc.shell_channel.execute(command, silent)

    def execute_file_ex(self, filetype, filename, args):
        if filetype == 'script':
            self.execute_file_as_main(filename, args)
        else:
            raise NotImplementedError("Cannot execute %s file" % filetype)

    def exit_process(self):
        self.exit_lock.release()

    def get_members(self, expression):
        """returns a tuple of the type name, instance members, and type members"""
        text = (expression + '.') if expression else ''
        if is_ipython_versionorgreater(3, 0):
            self.kc.complete(text)
        else:
            self.kc.shell_channel.complete(text, text, 1)

        self.members_lock.acquire()

        reply = self.complete_reply

        res = {}
        text_len = len(text)
        for member in reply['matches']:
            m_name = member[text_len:]
            if not any(c in m_name for c in '%!?-.,'):
                res[m_name] = 'object'

        return 'unknown', res, {}

    def get_signatures(self, expression):
        """returns doc, args, vargs, varkw, defaults."""
        
        if is_ipython_versionorgreater(3, 0):
            self.kc.inspect(expression, None, 2)
        else:
            self.kc.shell_channel.object_info(expression)
        
        self.members_lock.acquire()
        
        reply = self.object_info_reply 
        if is_ipython_versionorgreater(3, 0):
            data = reply['data']
            text = data['text/plain']
            text = remove_escapes.sub('', text)
            return [(text, (), None, None, [])]
        else:
            argspec = reply['argspec']
            defaults = argspec['defaults']
            if defaults is not None:
                defaults = [repr(default) for default in defaults]
            else:
                defaults = []
            return [(reply['docstring'], argspec['args'], argspec['varargs'], argspec['varkw'], defaults)]

    def interrupt_main(self):
        """aborts the current running command"""
        self.km.interrupt_kernel()
        
    def set_current_module(self, module):
        pass
        
    def get_module_names(self):
        """returns a list of module names"""
        return []

    def flush(self):
        pass

def init_debugger(self):
    import sys
    import debugpy
    from os import path
    
    # Set logging directory
    os.environ["DEBUGPY_LOG_DIR"] = "./debugpy_logs"

    debugpy.log_to("debugpy.log")
    sys.path.append(''' + repr(path.dirname(__file__)) + ''')
    
    # Configure the debugger    
    debugpy.listen(('127.0.0.1', 5678))  # Replace 5678 with your desired debug port if necessary
    print("Waiting for debugger to attach...")
    debugpy.wait_for_client()
    print("Debugger is attached.")


def attach_process(self, port, debugger_id):
    self.run_command(f'''
def __visualstudio_debugger_attach():
    import debugpy

    # Attach to the process
    try:
        debugpy.connect(('127.0.0.1', 5678))
        print(f"Attached to process with debugger ID: {debugger_id}")
    except Exception as e:
        print(f"Failed to attach debugger: {e}")

__visualstudio_debugger_attach()
del __visualstudio_debugger_attach
''', True)

class IPythonBackendWithoutPyLab(IPythonBackend):
    def get_extra_arguments(self):
        return []
