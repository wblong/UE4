#include"TestC.h"

int TestCFunc(int x, int y)
{
	return x * y;
}

extern "C" void IMPLEMENT_MODULE_TestC() { }